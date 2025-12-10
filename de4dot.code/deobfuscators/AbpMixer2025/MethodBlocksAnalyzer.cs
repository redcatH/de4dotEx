/*
    Copyright (C) 2011-2015 de4dot@gmail.com

    This file is part of de4dot.

    de4dot is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    de4dot is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with de4dot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using de4dot.blocks;
using de4dot.code;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.AbpMixer2025 {
	/// <summary>
	/// 方法块分析器：在所有块级优化完成后，对整个方法的块结构进行全局分析
	/// 用于：
	/// 1. 可达性分析（基于 CFG）
	/// 2. Opaque Predicate 识别（基于常量字段值）
	/// 3. 伪初始化块检测
	/// 4. 块重建指导信息生成
	/// 
	/// 与 ConstructorCleaner 的区别：
	/// - ConstructorCleaner: 块级优化（DeobfuscateBegin/Deobfuscate）
	/// - MethodBlocksAnalyzer: 全局分析（在块优化完成后调用）
	/// </summary>
	internal class MethodBlocksAnalyzer {
		private Deobfuscator _owner;

		internal MethodBlocksAnalyzer(Deobfuscator owner) {
			this._owner = owner;
		}

		/// <summary>
		/// 分析单个方法的所有块结构
		/// 在 DeobfuscateEnd 阶段调用，此时块级优化已完成
		/// </summary>
		internal void AnalyzeMethod(MethodDef method) {
			if (method == null || method.Body == null || method.Body.HasInstructions == false) {
				return;
			}

			// 为避免创建 Blocks 对象（那需要转换），我们直接处理原始 IL
			// 如果需要块视图，可以创建临时的 Blocks 对象
			var blocks = new Blocks(method);
			if (!Logger.Instance.IgnoresEvent(LoggerEvent.VeryVerbose)) {
				DumpMethodIL(blocks, "优化前");
			}

			// 第一步：基于控制流图的可达性分析
			AnalyzeBlockReachability(blocks);

			// 第二步：基于 Opaque Predicates 的可达性优化
			AnalyzeOpaquePredicates(blocks);

			// 第三步：识别伪初始化块
			AnalyzePseudoInitializationBlocks(blocks);

			// 第四步：分析常量条件
			AnalyzeConstantConditions(blocks);

			// 第五步：分析嵌套循环模式
			AnalyzeNestedLoopsPattern(blocks);

			// 第六步：简化虚假常数条件分支（通用策略，适用所有方法）
			SimplifyConstantBranchBlocks(blocks);

			// 第七步：简化异步状态机初始化（实际块修改）
			SimplifyAsyncStateMachineInitialization(blocks);

			// 重建 IL 以反映块修改
			blocks.GetCode(out var allInstructions, out var allExceptionHandlers);
			blocks.Method.Body.Instructions.Clear();
			foreach (var instr in allInstructions) {
				blocks.Method.Body.Instructions.Add(instr);
			}

			blocks.Method.Body.ExceptionHandlers.Clear();
			foreach (var eh in allExceptionHandlers) {
				blocks.Method.Body.ExceptionHandlers.Add(eh);
			}

			// 第七步：输出完整的 IL 代码（用于调试）
			if (!Logger.Instance.IgnoresEvent(LoggerEvent.VeryVerbose)) {
				DumpMethodIL(blocks, "优化后");
			}
		}

		/// <summary>
		/// 第一步：分析块的可达性和可删除性
		/// 构建控制流图，计算每个块的可达性
		/// 同时识别"空块"（只包含无实际意义的操作）
		/// </summary>
		void AnalyzeBlockReachability(Blocks blocks) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();

			Logger.v("\n========== 方法块可达性分析 ==========");
			Logger.v("方法: {0}", blocks.Method.Name);
			Logger.v("总块数: {0}", allBlocks.Count);

			// 1. 识别入口块
			var entryBlock = allBlocks.Count > 0 ? allBlocks[0] : null;
			if (entryBlock == null) {
				Logger.v("未找到入口块");
				return;
			}

			// 2. BFS 遍历计算可达性
			var reachable = new HashSet<Block>();
			var queue = new Queue<Block>();
			queue.Enqueue(entryBlock);
			reachable.Add(entryBlock);

			while (queue.Count > 0) {
				var block = queue.Dequeue();

				// 检查块的后继
				// 1. FallThrough 后继
				if (block.FallThrough != null && !reachable.Contains(block.FallThrough)) {
					reachable.Add(block.FallThrough);
					queue.Enqueue(block.FallThrough);
				}

				// 2. 显式分支目标
				if (block.Targets != null) {
					foreach (var target in block.Targets) {
						if (!reachable.Contains(target)) {
							reachable.Add(target);
							queue.Enqueue(target);
						}
					}
				}
			}

			// 3. 分类块
			var reachableList = new List<Block>();
			var unreachableList = new List<Block>();
			var emptyReachableList = new List<Block>();
			var emptyUnreachableList = new List<Block>();

			foreach (var block in allBlocks) {
				bool isReachable = reachable.Contains(block);
				bool isEmpty = IsEmptyBlock(block);

				if (isReachable && isEmpty) {
					emptyReachableList.Add(block);
				}
				else if (isReachable) {
					reachableList.Add(block);
				}
				else if (isEmpty) {
					emptyUnreachableList.Add(block);
				}
				else {
					unreachableList.Add(block);
				}
			}

			// 4. 输出分析结果
			Logger.v("可达块: {0} 个 (其中空块: {1} 个)", reachableList.Count, emptyReachableList.Count);
			Logger.v("不可达块: {0} 个 (其中空块: {1} 个)", unreachableList.Count, emptyUnreachableList.Count);

			// 可达的空块（可以删除但需要保留连接）
			if (emptyReachableList.Count > 0) {
				Logger.v("\n可达的空块（可删除，前后连接）:");
				foreach (var block in emptyReachableList) {
					uint offset = block.Instructions.Count > 0 ? block.Instructions[0].Instruction.Offset : 0;
					Logger.v("  Block IL_{0:X4}: 指令数={1}", offset, block.Instructions.Count);
				}
			}

			// 不可达块（直接删除）
			if (unreachableList.Count > 0) {
				Logger.v("\n不可达块（直接删除）:");
				foreach (var block in unreachableList) {
					uint offset = block.Instructions.Count > 0 ? block.Instructions[0].Instruction.Offset : 0;
					Logger.v("  Block IL_{0:X4}: 指令数={1}", offset, block.Instructions.Count);
				}
			}

			// 完整的空块分布
			if (emptyUnreachableList.Count > 0) {
				Logger.v("\n不可达的空块（已经死的）:");
				foreach (var block in emptyUnreachableList) {
					uint offset = block.Instructions.Count > 0 ? block.Instructions[0].Instruction.Offset : 0;
					Logger.v("  Block IL_{0:X4}: 指令数={1}", offset, block.Instructions.Count);
				}
			}

			Logger.v("========== 方法块可达性分析结束 ==========\n");
		}

		/// <summary>
		/// 第二步：分析 Opaque Predicates 对可达性的影响
		/// 使用 _owner.FieldValues 中的常量值，重新评估条件分支的可达性
		/// 同时识别异步状态机模式
		/// </summary>
		void AnalyzeOpaquePredicates(Blocks blocks) {
			if (_owner.OpaquePredicateFields.Count == 0) {
				Logger.v("[OpaquePredicates] 未检测到不透明谓词字段，跳过分析");
			}
			else {
				Logger.v("\n========== Opaque Predicate 分析 ==========");
				Logger.v("已知的不透明谓词字段数: {0}", _owner.OpaquePredicateFields.Count);

				var allBlocks = blocks.MethodBlocks.GetAllBlocks();
				int predicatesFound = 0;

				// 遍历所有块，查找基于 OpaquePredicateFields 的分支
				foreach (var block in allBlocks) {
					for (int i = 0; i < block.Instructions.Count; i++) {
						var instr = block.Instructions[i].Instruction;

						// 检查条件分支
						if (IsConditionalBranch(instr)) {
							// 查看是否涉及 OpaquePredicateFields
							if (CheckIfInvolvesOpaquePredicates(block, i)) {
								predicatesFound++;
								Logger.vv("[OpaquePredicates] 块 IL_{0:X4} 包含不透明谓词分支",
									block.Instructions.Count > 0 ? block.Instructions[0].Instruction.Offset : 0);
							}
						}
					}
				}

				Logger.v("[OpaquePredicates] 检测到 {0} 处可能的不透明谓词分支", predicatesFound);
				Logger.v("========== Opaque Predicate 分析结束 ==========\n");
			}

			// 检测异步状态机模式
			DetectAsyncStateMachinePattern(blocks);
		}

		Local GetLocalFromStloc(Instruction instr, IList<Local> locals) {
			if (instr == null) return null;
			var code = instr.OpCode.Code;
			if (code >= Code.Stloc_0 && code <= Code.Stloc_3) {
				int idx = code - Code.Stloc_0;
				return idx < locals.Count ? locals[idx] : null;
			}

			return instr.Operand as Local;
		}

		Local GetLocalFromLdloc(Instruction instr, IList<Local> locals) {
			if (instr == null) return null;
			var code = instr.OpCode.Code;
			if (code >= Code.Ldloc_0 && code <= Code.Ldloc_3) {
				int idx = code - Code.Ldloc_0;
				return idx < locals.Count ? locals[idx] : null;
			}

			return instr.Operand as Local;
		}

		/// <summary>
		/// 检测异步状态机模式
		/// 特征：
		/// 1. 方法返回 Task 或 Task&lt;T&gt;
		/// 2. 有一个局部变量被多个常量赋值（状态值）
		/// 3. 该变量用于 switch/beq 等条件分支
		/// 4. 存在 AsyncTaskMethodBuilder 相关调用
		/// </summary>
		void DetectAsyncStateMachinePattern(Blocks blocks) {
			var method = blocks.Method;
			var locals = blocks.Locals;

			// 检查返回值是否为 Task 或 Task<T>
			var returnType = method.ReturnType?.FullName ?? "";
			bool isTask = returnType.StartsWith("System.Threading.Tasks.Task", System.StringComparison.Ordinal);

			if (!isTask) {
				return; // 不是异步方法
			}

			Logger.v("\n========== 异步状态机模式检测 ==========");
			Logger.v("方法: {0} (返回值: {1})", method.Name, returnType);

			// 查找状态变量（被多个常量赋值，用于分支条件）
			var candidateStateVars = new Dictionary<Local, HashSet<int>>();

			foreach (var local in locals) {
				if (local.Type.ElementType != ElementType.I4) continue; // 只看 int 类型

				candidateStateVars[local] = new HashSet<int>();
			}

			// 扫描所有块，找出被赋值为常量且用于分支的局部变量
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			foreach (var block in allBlocks) {
				for (int i = 0; i < block.Instructions.Count; i++) {
					var instr = block.Instructions[i].Instruction;

					// 检查 stloc（赋值）
					if (instr.OpCode == OpCodes.Stloc || instr.OpCode == OpCodes.Stloc_S ||
					    (instr.OpCode.Code >= Code.Stloc_0 && instr.OpCode.Code <= Code.Stloc_3)) {
						var local = GetLocalFromStloc(instr, locals);
						if (local != null && candidateStateVars.ContainsKey(local) && i > 0) {
							var prevInstr = block.Instructions[i - 1].Instruction;
							if (IsLdcI4(prevInstr)) {
								int? value = ExtractLdcI4Value(prevInstr);
								if (value.HasValue) {
									candidateStateVars[local].Add(value.Value);
								}
							}
						}
					}

					// 检查 ldloc（使用在分支条件）
					if (IsConditionalBranch(instr) && i > 0) {
						var prevInstr = block.Instructions[i - 1].Instruction;
						if (prevInstr.OpCode == OpCodes.Ldloc || prevInstr.OpCode == OpCodes.Ldloc_S ||
						    (prevInstr.OpCode.Code >= Code.Ldloc_0 && prevInstr.OpCode.Code <= Code.Ldloc_3)) {
							var local = GetLocalFromLdloc(prevInstr, locals);
							if (local != null && candidateStateVars.ContainsKey(local)) {
								// 这个变量用于分支条件
							}
						}
					}
				}
			}

			// 输出发现的状态变量
			int stateVarsFound = 0;
			foreach (var kvp in candidateStateVars) {
				if (kvp.Value.Count > 2) {
					// 至少有 3 个不同的值
					stateVarsFound++;
					Logger.v("[AsyncStateMachine] 发现状态变量: {0}, 值: {{{1}}}",
						kvp.Key.Name ?? "local", string.Join(", ", kvp.Value.OrderBy(x => x)));
				}
			}

			if (stateVarsFound > 0) {
				Logger.v("[AsyncStateMachine] 这是一个异步状态机方法，有 {0} 个状态变量", stateVarsFound);
			}

			Logger.v("========== 异步状态机模式检测结束 ==========\n");
		}

		bool IsLdcI4(Instruction instr) {
			if (instr == null) return false;
			var code = instr.OpCode.Code;
			return (code >= Code.Ldc_I4_M1 && code <= Code.Ldc_I4_8) ||
			       code == Code.Ldc_I4 || code == Code.Ldc_I4_S;
		}

		int? ExtractLdcI4Value(Instruction instr) {
			if (instr == null) return null;
			var code = instr.OpCode.Code;

			// ldc.i4.m1 .. ldc.i4.8
			if (code >= Code.Ldc_I4_M1 && code <= Code.Ldc_I4_8) {
				return (int)code - (int)Code.Ldc_I4_0;
			}

			// ldc.i4 <value>
			if (code == Code.Ldc_I4) {
				return instr.Operand as int?;
			}

			// ldc.i4.s <value>
			if (code == Code.Ldc_I4_S) {
				return (int?)(instr.Operand as sbyte?);
			}

			return null;
		}

		/// <summary>
		/// 第三步：识别伪初始化块
		/// 这些块只存在于初始化路径中，可以被优化掉
		/// </summary>
		void AnalyzePseudoInitializationBlocks(Blocks blocks) {
			Logger.v("\n========== 伪初始化块识别 ==========");

			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			int pseudoInitBlocks = 0;

			// 简单策略：第一个块如果是空的，可能就是初始化块
			if (allBlocks.Count > 0 && IsEmptyBlock(allBlocks[0])) {
				pseudoInitBlocks++;
				Logger.v("[PseudoInit] 块 0 可能是伪初始化块（空块）");
			}

			// 识别只被初始化块引用的块
			var referencedByInit = new HashSet<Block>();
			if (allBlocks.Count > 0) {
				var firstBlock = allBlocks[0];
				if (firstBlock.FallThrough != null) {
					referencedByInit.Add(firstBlock.FallThrough);
				}

				if (firstBlock.Targets != null) {
					foreach (var target in firstBlock.Targets) {
						referencedByInit.Add(target);
					}
				}
			}

			Logger.v("[PseudoInit] 识别到 {0} 个伪初始化块", pseudoInitBlocks);
			Logger.v("========== 伪初始化块识别结束 ==========\n");
		}

		/// <summary>
		/// 检查块中的指令是否涉及 OpaquePredicateFields
		/// </summary>
		bool CheckIfInvolvesOpaquePredicates(Block block, int instrIndex) {
			if (instrIndex == 0) return false;

			var instr = block.Instructions[instrIndex].Instruction;
			var prevInstr = block.Instructions[instrIndex - 1].Instruction;

			// 简单检查：前一个指令是否加载了 OpaquePredicateField
			if (prevInstr.OpCode == OpCodes.Ldfld) {
				var field = prevInstr.Operand as IField;
				if (field != null) {
					var resolved = _owner.ResolveTargetField(field);
					if (resolved != null && _owner.OpaquePredicateFields.Contains(resolved)) {
						return true;
					}
				}
			}

			return false;
		}

		/// <summary>
		/// 判断块是否为空块（只包含无实际意义的操作）
		/// 空块：
		/// 1. 无指令
		/// 2. 只有 nop / pop / ldc.i4 这样的无用指令
		/// </summary>
		bool IsEmptyBlock(Block block) {
			if (block.Instructions.Count == 0)
				return true;

			// 检查是否全是无用指令
			foreach (var instr in block.Instructions) {
				var code = instr.Instruction.OpCode.Code;

				// 有用的操作码：修改状态、调用方法、返回等
				if (code == Code.Stloc || code == Code.Stloc_S ||
				    (code >= Code.Stloc_0 && code <= Code.Stloc_3) ||
				    code == Code.Stfld || code == Code.Stsfld ||
				    code == Code.Call || code == Code.Callvirt ||
				    code == Code.Ret ||
				    code == Code.Throw ||
				    code == Code.Newobj) {
					return false; // 有实际操作，不是空块
				}
			}

			// 全是无用指令（加载、nop、pop 等）
			return true;
		}

		bool IsConditionalBranch(Instruction instr) {
			var code = instr?.OpCode.Code ?? Code.Nop;
			return code == Code.Brtrue || code == Code.Brtrue_S ||
			       code == Code.Brfalse || code == Code.Brfalse_S ||
			       code == Code.Switch ||
			       code == Code.Beq || code == Code.Beq_S ||
			       code == Code.Bne_Un || code == Code.Bne_Un_S ||
			       code == Code.Blt || code == Code.Blt_S ||
			       code == Code.Bgt || code == Code.Bgt_S ||
			       code == Code.Ble || code == Code.Ble_S ||
			       code == Code.Bge || code == Code.Bge_S ||
			       code == Code.Blt_Un || code == Code.Blt_Un_S ||
			       code == Code.Bgt_Un || code == Code.Bgt_Un_S ||
			       code == Code.Ble_Un || code == Code.Ble_Un_S ||
			       code == Code.Bge_Un || code == Code.Bge_Un_S;
		}

		/// <summary>
		/// 输出完整的方法 IL 代码用于分析
		/// 用法：在运行时添加 -vv 标志查看完整 IL 代码
		/// 这是在块优化完成后的最终 IL 状态
		/// </summary>
		void DumpMethodIL(Blocks blocks, string message) {
			var method = blocks.Method;
			Logger.vv($"\n========== 方法{message}的 IL 代码 ==========");
			Logger.vv("方法: {0}", method.FullName);

			// 获取所有块（使用 GetAllBlocks）
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			Logger.vv("Block 数量: {0}", allBlocks.Count);
			Logger.vv("局部变量数量: {0}", blocks.Locals.Count);
			Logger.vv("");

			// 遍历所有块
			int blockIdx = 0;
			foreach (var block in allBlocks) {
				// 获取块的第一个指令偏移
				uint blockOffset = block.Instructions.Count > 0 ? block.Instructions[0].Instruction.Offset : 0;
				Logger.vv("--- Block {0} (Label: IL_{1:X4}) ---", blockIdx, blockOffset);

				// 输出块内所有指令
				for (int i = 0; i < block.Instructions.Count; i++) {
					var instr = block.Instructions[i].Instruction;
					string offset = $"IL_{instr.Offset:X4}";
					Logger.vv("  {0}: {1}", offset, FormatInstruction(instr));
				}

				// 输出块的目标
				if (block.Targets?.Count > 0) {
					Logger.vv("  → 目标: {0}", string.Join(", ",
						block.Targets.Select(t => {
							uint targetOffset = t.Instructions.Count > 0 ? t.Instructions[0].Instruction.Offset : 0;
							return $"IL_{targetOffset:X4}";
						})));
				}

				Logger.vv("");
				blockIdx++;
			}

			Logger.vv($"========== 方法{message}的 IL 代码结束 ==========\n");
		}

		/// <summary>
		/// 第五步：分析常量条件
		/// 跟踪局部变量的值，识别基于常量的分支
		/// </summary>
		private void AnalyzeConstantConditions(Blocks blocks) {
			Logger.v("\n========== 常量条件分析 ==========");

			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			if (allBlocks.Count == 0) return;

			// 为每个块维护一个"活跃常量"集合
			// 变量 Index -> 已知常量值集合
			var blockConstValues = new Dictionary<Block, Dictionary<int, HashSet<int>>>();

			// 初始化起始块
			var entryBlock = allBlocks[0];
			blockConstValues[entryBlock] = new Dictionary<int, HashSet<int>>();

			// 工作列表：需要处理的块
			var worklist = new Queue<Block>();
			worklist.Enqueue(entryBlock);
			var processed = new HashSet<Block>();

			while (worklist.Count > 0) {
				var block = worklist.Dequeue();
				if (processed.Contains(block)) continue;
				processed.Add(block);

				if (!blockConstValues.ContainsKey(block)) {
					blockConstValues[block] = new Dictionary<int, HashSet<int>>();
				}

				var localVarValues = blockConstValues[block];
				var origVarState = new Dictionary<int, HashSet<int>>(localVarValues);

				// 扫描块中的指令，跟踪常量赋值
				foreach (var instr in block.Instructions) {
					// 识别: ldc.i4 X; stloc Y 模式
					// 注意：instr 是 de4dot.blocks.Instr 对象，需要用 .Instruction 属性获取 dnlib Instruction
					if (IsLoadConstant(instr.Instruction, out int constValue)) {
						// 下一条指令应该是 stloc
						var idx = block.Instructions.IndexOf(instr);
						if (idx + 1 < block.Instructions.Count) {
							var nextInstr = block.Instructions[idx + 1];
							if (IsStloc(nextInstr.Instruction, out int localIndex)) {
								// 记录: local_localIndex = constValue
								if (!localVarValues.ContainsKey(localIndex)) {
									localVarValues[localIndex] = new HashSet<int>();
								}

								localVarValues[localIndex].Add(constValue);
								Logger.v("[ConstantProp] 局部变量 local{0} = {1}", localIndex, constValue);
							}
						}
					}
				}

				// 检查分支条件
				if (block.FallThrough != null) {
					// 无条件跳转，传播所有已知常量
					if (!blockConstValues.ContainsKey(block.FallThrough)) {
						blockConstValues[block.FallThrough] = new Dictionary<int, HashSet<int>>(localVarValues);
						worklist.Enqueue(block.FallThrough);
					}
				}

				if (block.Targets != null && block.Targets.Count > 0) {
					// 有条件/无条件分支
					// 检查最后一条指令：是否为 brfalse/brtrue/switch
					if (block.Instructions.Count > 0) {
						var lastInstr = block.Instructions[block.Instructions.Count - 1];
						var lastIL = lastInstr.Instruction;

						if (lastIL.OpCode == OpCodes.Brfalse || lastIL.OpCode == OpCodes.Brfalse_S) {
							Logger.v("[ConstantCond] brfalse 条件");
							// 如果条件值已知，可优化
							if (block.Instructions.Count >= 2) {
								var prevInstr = block.Instructions[block.Instructions.Count - 2];
								if (IsLoadInstr(prevInstr.Instruction, out int loadIndex)) {
									if (localVarValues.ContainsKey(loadIndex)) {
										var values = localVarValues[loadIndex];
										Logger.v("[ConstantCond] 常量分支可简化: local{0} 值为 {1}",
											loadIndex, string.Join(",", values));
									}
								}
							}
						}
						else if (lastIL.OpCode == OpCodes.Switch) {
							Logger.v("[ConstantCond] switch 条件");
							if (block.Instructions.Count >= 2) {
								var prevInstr = block.Instructions[block.Instructions.Count - 2];
								if (IsLoadInstr(prevInstr.Instruction, out int switchIndex)) {
									if (localVarValues.ContainsKey(switchIndex)) {
										var values = localVarValues[switchIndex];
										Logger.v("[ConstantCond] switch 操作数可简化: local{0} 值为 {1}",
											switchIndex, string.Join(",", values));
									}
								}
							}
						}
					} // 将所有分支目标加入工作列表

					foreach (var target in block.Targets) {
						if (!blockConstValues.ContainsKey(target)) {
							blockConstValues[target] = new Dictionary<int, HashSet<int>>(localVarValues);
							worklist.Enqueue(target);
						}
					}
				}
			}

			Logger.v("========== 常量条件分析结束 ==========\n");
		}

		/// <summary>
		/// 第六步：分析嵌套循环模式
		/// 检测 while(true) 循环及其嵌套结构
		/// 识别可以简化的冗余循环
		/// </summary>
		private void AnalyzeNestedLoopsPattern(Blocks blocks) {
			Logger.v("\n========== 嵌套循环模式分析 ==========");

			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			if (allBlocks.Count == 0) return;

			// 识别循环头：有反向边（来自更后面块的跳转）
			var loopHeads = new HashSet<Block>();
			var backEdges = new List<(Block from, Block to)>();

			foreach (var block in allBlocks) {
				if (block.Targets != null) {
					foreach (var target in block.Targets) {
						// 反向边：从当前块跳回到之前的块
						if (allBlocks.IndexOf(block) > allBlocks.IndexOf(target)) {
							loopHeads.Add(target);
							backEdges.Add((block, target));
						}
					}
				}
			}

			Logger.v("检测到 {0} 个潜在循环头，{1} 条反向边", loopHeads.Count, backEdges.Count);

			// 对每个循环头，分析其循环体
			foreach (var loopHead in loopHeads) {
				Logger.v("\n[NestedLoops] 循环头: Block_{0:X}", loopHead.GetHashCode());

				// 找出到达这个循环头的块（循环体的最后）
				var predecessors = new List<Block>();
				foreach (var block in allBlocks) {
					if (block.FallThrough == loopHead ||
					    (block.Targets != null && block.Targets.Contains(loopHead))) {
						predecessors.Add(block);
					}
				}

				Logger.v("[NestedLoops] 循环后驱: {0} 个块", predecessors.Count);

				// 检查循环头的指令：是否为 while(true) 模式
				if (loopHead.Instructions.Count > 0) {
					var lastInstr = loopHead.Instructions[loopHead.Instructions.Count - 1];

					// while(true) 应该是无条件跳转到某个块
					if (lastInstr.OpCode == OpCodes.Br || lastInstr.OpCode == OpCodes.Br_S) {
						Logger.v("[NestedLoops] while(true) 模式确认");
					}
					else if (lastInstr.OpCode == OpCodes.Brfalse || lastInstr.OpCode == OpCodes.Brfalse_S ||
					         lastInstr.OpCode == OpCodes.Brtrue || lastInstr.OpCode == OpCodes.Brtrue_S) {
						Logger.v("[NestedLoops] 条件循环模式");
					}
				}

				// 计算循环体大小（块数）
				var loopBodySize = 0;
				var visited = new HashSet<Block>();
				var stack = new Stack<Block>();
				stack.Push(loopHead);

				while (stack.Count > 0) {
					var block = stack.Pop();
					if (visited.Contains(block)) continue;
					visited.Add(block);
					loopBodySize++;

					// 沿着 FallThrough 和 Targets 走，但不跟随反向边
					if (block.FallThrough != null && allBlocks.IndexOf(block) < allBlocks.IndexOf(block.FallThrough)) {
						if (!visited.Contains(block.FallThrough)) {
							stack.Push(block.FallThrough);
						}
					}

					if (block.Targets != null) {
						foreach (var target in block.Targets) {
							if (allBlocks.IndexOf(block) < allBlocks.IndexOf(target) && !visited.Contains(target)) {
								stack.Push(target);
							}
						}
					}
				}

				Logger.v("[NestedLoops] 循环体大小: {0} 个块", loopBodySize);
			}

			Logger.v("========== 嵌套循环模式分析结束 ==========\n");
		}

		/// <summary>
		/// 第七步：简化 ABP Mixer 异步状态机初始化模式
		/// 识别并删除虚假的控制流，线性化初始化序列
		/// 
		/// 典型模式：
		///   Block 0: ldc.i4 3; stloc 1
		///   Block 1: builder.Create(); ldc.i4 2; stloc 1
		///   Block 2: set this/id; ldc.i4 11; stloc 1
		///   Block 3: ldloc 1; ldc.i4 11; beq.s (总是跳转)
		///   Block 4: empty (虚假分支)
		///   Block 5: 实际继续
		///   Block 8: ldloc 1; switch (虚假循环)
		///   Block 10: ldloc 1; ldc.i4 992; beq.s (伪造返回)
		/// 
		/// 目标：简化为线性执行顺序
		/// </summary>
		
		/// <summary>
		/// 简化虚假常数条件分支块（通用策略）
		/// 适用于所有方法（构造函数、方法、异步方法等）
		/// 
		/// 虚假块特征：
		/// 1. 常量条件分支：ldloc X; ldc.i4 C; beq.s target（比较两个相同的常量值）
		/// 2. switch 语句：ldloc X; switch targets（控制流虚假分支）
		/// 3. 伪造返回：ldloc X; ldc.i4 992; beq.s target（不可能达到的返回检查）
		/// </summary>
		private void SimplifyConstantBranchBlocks(Blocks blocks) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			Logger.v("\n========== 虚假常数条件分支简化 ==========");

			// 扫描所有块，识别虚假控制流块及其正确的后继
			var falseBranchBlocks = new List<(Block block, Block correctNext)>();
			var switchBlocks = new List<Block>();

			for (int i = 0; i < allBlocks.Count; i++) {
				var block = allBlocks[i];
				if (block.Instructions.Count == 0) continue;

				var lastInstr = block.Instructions[block.Instructions.Count - 1];
				var lastIL = lastInstr.Instruction;

				// 模式 1：常量条件分支 (beq/beq.s)
				if ((lastIL.OpCode == OpCodes.Beq || lastIL.OpCode == OpCodes.Beq_S) &&
				    block.Instructions.Count >= 3) {
					var secondLast = block.Instructions[block.Instructions.Count - 2];
					
					if (IsLoadConstant(secondLast.Instruction, out int constVal)) {
						if (lastIL.Operand is Instruction targetInstr) {
							Block targetBlock = null;
							for (int j = 0; j < allBlocks.Count; j++) {
								var potentialTarget = allBlocks[j];
								if (potentialTarget.Instructions.Count > 0 &&
									potentialTarget.Instructions[0].Instruction.Offset == targetInstr.Offset) {
									targetBlock = potentialTarget;
									break;
								}
							}

							if (targetBlock != null) {
								if (constVal == 992) {
									Logger.v("[ConstBranch] 发现伪造返回块 Block {0}", i);
									falseBranchBlocks.Add((block, targetBlock));
								}
								else if (constVal > 0 && block.Instructions.Count == 3) {
									Logger.v("[ConstBranch] 发现常量条件块 Block {0} (ldc.i4 {1})", i, constVal);
									falseBranchBlocks.Add((block, targetBlock));
								}
							}
						}
					}
				}

				// 模式 2：switch 语句块
				if (lastIL.OpCode == OpCodes.Switch && block.Instructions.Count >= 2) {
					switchBlocks.Add(block);
					Logger.v("[ConstBranch] 发现 switch 块 Block {0}", i);
				}
			}

			int totalFalseBlocks = falseBranchBlocks.Count + switchBlocks.Count;
			if (totalFalseBlocks == 0) {
				Logger.v("[ConstBranch] 未找到虚假常数条件分支");
				Logger.v("========== 虚假常数条件分支简化结束 ==========\n");
				return;
			}

			Logger.v("[ConstBranch] 检测到虚假块数: {0} (beq: {1}, switch: {2})",
				totalFalseBlocks, falseBranchBlocks.Count, switchBlocks.Count);

			// 建立虚假块到正确目标的映射
			var falseBlockToTarget = new Dictionary<Block, Block>();
			foreach (var (falseBlock, targetBlock) in falseBranchBlocks) {
				falseBlockToTarget[falseBlock] = targetBlock;
			}

			// 重定向所有指向虚假块的块
			Logger.v("[ConstBranch] 开始重定向前驱...");
			foreach (var block in allBlocks) {
				if (block == null) continue;

				if (block.FallThrough != null && falseBlockToTarget.ContainsKey(block.FallThrough)) {
					Block oldTarget = block.FallThrough;
					Block newTarget = falseBlockToTarget[oldTarget];
					block.FallThrough = newTarget;
					Logger.v("[ConstBranch] 重定向 FallThrough");
				}

				if (block.Targets != null) {
					for (int i = 0; i < block.Targets.Count; i++) {
						if (falseBlockToTarget.ContainsKey(block.Targets[i])) {
							Block oldTarget = block.Targets[i];
							Block newTarget = falseBlockToTarget[oldTarget];
							block.Targets[i] = newTarget;
							Logger.v("[ConstBranch] 重定向 Target");
						}
					}
				}
			}

			// 清空虚假块的指令
			Logger.v("[ConstBranch] 开始清空虚假块指令...");
			foreach (var (falseBlock, _) in falseBranchBlocks) {
				falseBlock.Instructions.Clear();
				falseBlock.Targets = null;
			}

			foreach (var switchBlock in switchBlocks) {
				switchBlock.Instructions.Clear();
				switchBlock.Targets = null;
			}

			Logger.v("[ConstBranch] 虚假常数条件分支简化完成");
			Logger.v("========== 虚假常数条件分支简化结束 ==========\n");
		}

		/// <summary>
		/// 简化 MoveNext 方法的虚假控制流
		/// MoveNext 是编译器生成的状态机方法，其结构与普通 async 方法不同
		/// 
		/// 关键策略：
		/// 1. 虚假块是那些"条件总是成立"的分支块，例如 beq.s 在比较相同的值时
		/// 2. 这些块的分支指令总是跳转（beq 11 vs 11），所以 FallThrough 是假的
		/// 3. 我们需要的是让虚假块跳过掉，指向正确的业务代码块
		/// </summary>
		private void SimplifyMoveNextBlocks(Blocks blocks) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			Logger.v("[SimplifyAsync] 开始简化 MoveNext 方法...");

			// 阶段 1：扫描所有块，识别虚假控制流块及其正确的后继
			var falseBranchBlocks = new List<(Block block, Block correctNext)>(); // 常量条件分支块及其目标
			var switchBlocks = new List<Block>();      // switch 语句块

			for (int i = 0; i < allBlocks.Count; i++) {
				var block = allBlocks[i];
				if (block.Instructions.Count == 0) continue;

				var lastInstr = block.Instructions[block.Instructions.Count - 1];
				var lastIL = lastInstr.Instruction;

				// 模式 1：常量条件分支 (beq/beq.s)
				// 形式：ldloc X; ldc.i4 C; beq.s target
				if ((lastIL.OpCode == OpCodes.Beq || lastIL.OpCode == OpCodes.Beq_S) &&
				    block.Instructions.Count >= 3) {
					var secondLast = block.Instructions[block.Instructions.Count - 2];
					
					// 检查是否为常量条件
					if (IsLoadConstant(secondLast.Instruction, out int constVal)) {
						// beq 指令的操作数是跳转目标
						if (lastIL.Operand is Instruction targetInstr) {
							// 找到目标块
							Block targetBlock = null;
							for (int j = 0; j < allBlocks.Count; j++) {
								var potentialTarget = allBlocks[j];
								if (potentialTarget.Instructions.Count > 0 &&
									potentialTarget.Instructions[0].Instruction.Offset == targetInstr.Offset) {
									targetBlock = potentialTarget;
									break;
								}
							}

							if (targetBlock != null) {
								if (constVal == 992) {
									// 伪造返回模式：这个块会被删除但我们记录其目标
									Logger.v("[SimplifyAsync] 发现伪造返回块 Block {0} -> Block (offset {1})", i, targetInstr.Offset);
									falseBranchBlocks.Add((block, targetBlock));
								}
								else if (constVal > 0 && block.Instructions.Count == 3) {
									// 常态条件分支：清空此块并让前驱指向正确的目标
									Logger.v("[SimplifyAsync] 发现常量条件块 Block {0} -> Block (offset {1})", i, targetInstr.Offset);
									falseBranchBlocks.Add((block, targetBlock));
								}
							}
						}
					}
				}

				// 模式 2：switch 语句块
				if (lastIL.OpCode == OpCodes.Switch && block.Instructions.Count >= 2) {
					switchBlocks.Add(block);
					Logger.v("[SimplifyAsync] 发现 switch 块 Block {0}", i);
				}
			}

			int totalFalseBlocks = falseBranchBlocks.Count + switchBlocks.Count;
			if (totalFalseBlocks == 0) {
				Logger.v("[SimplifyAsync] 未找到虚假控制流模式");
				return;
			}

			Logger.v("[SimplifyAsync] 检测到虚假块数: {0} (beq: {1}, switch: {2})",
				totalFalseBlocks, falseBranchBlocks.Count, switchBlocks.Count);

			// 阶段 2：重定向所有指向虚假块的块
			Logger.v("[SimplifyAsync] 开始重定向指向虚假块的所有前驱...");

			// 建立虚假块到正确目标的映射
			var falseBlockToTarget = new Dictionary<Block, Block>();
			foreach (var (falseBlock, targetBlock) in falseBranchBlocks) {
				falseBlockToTarget[falseBlock] = targetBlock;
			}

			// 遍历所有块，找出指向虚假块的引用
			foreach (var block in allBlocks) {
				if (block == null) continue;

				// 检查 FallThrough
				if (block.FallThrough != null && falseBlockToTarget.ContainsKey(block.FallThrough)) {
					Block oldTarget = block.FallThrough;
					Block newTarget = falseBlockToTarget[oldTarget];
					block.FallThrough = newTarget;
					Logger.v("[SimplifyAsync] 重定向 FallThrough: 虚假块 -> {0}", newTarget.GetHashCode());
				}

				// 检查 Targets（分支目标）
				if (block.Targets != null) {
					for (int i = 0; i < block.Targets.Count; i++) {
						if (falseBlockToTarget.ContainsKey(block.Targets[i])) {
							Block oldTarget = block.Targets[i];
							Block newTarget = falseBlockToTarget[oldTarget];
							block.Targets[i] = newTarget;
							Logger.v("[SimplifyAsync] 重定向 Target: 虚假块 -> {0}", newTarget.GetHashCode());
						}
					}
				}
			}

			// 阶段 3：清空虚假块的指令（但保持块存在，避免块结构破坏）
			Logger.v("[SimplifyAsync] 开始清空虚假块的指令...");

			foreach (var (falseBlock, _) in falseBranchBlocks) {
				int instrCount = falseBlock.Instructions.Count;
				falseBlock.Instructions.Clear();
				falseBlock.Targets = null; // 清除分支目标
				Logger.v("[SimplifyAsync] 清空常量条件块 ({0} 条指令删除)", instrCount);
			}

			// 清空 switch 块
			foreach (var switchBlock in switchBlocks) {
				int instrCount = switchBlock.Instructions.Count;
				switchBlock.Instructions.Clear();
				switchBlock.Targets = null;
				Logger.v("[SimplifyAsync] 清空 switch 块 ({0} 条指令删除)", instrCount);
			}

			Logger.v("[SimplifyAsync] MoveNext 虚假控制流删除完成");
		}

		private void SimplifyAsyncStateMachineInitialization(Blocks blocks) {
			Logger.v("\n========== 异步状态机初始化简化 ==========");

			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			if (allBlocks.Count < 10) {
				Logger.v("块数不足，跳过简化");
				Logger.v("========== 异步状态机初始化简化结束 ==========\n");
				return;
			}

			var method = blocks.Method;

			// 检查是否为异步方法
			if (!IsAsyncMethod(method)) {
				Logger.v("非异步方法，跳过简化");
				Logger.v("========== 异步状态机初始化简化结束 ==========\n");
				return;
			}

			// 区分处理 MoveNext 和普通 Async 方法
			bool isMoveNext = method.Name == "MoveNext";
			
			if (isMoveNext) {
				// MoveNext 方法：编译器生成的状态机方法
				Logger.v("[SimplifyAsync] 处理 MoveNext 方法");
				try {
					SimplifyMoveNextBlocks(blocks);
					Logger.v("[SimplifyAsync] MoveNext 块简化成功");
				}
				catch (Exception ex) {
					Logger.v("[SimplifyAsync] MoveNext 块简化失败: {0}", ex.Message);
				}
			}
			else if (method.Name.EndsWith("Async", System.StringComparison.Ordinal)) {
				// 普通 Async 方法
				Logger.v("[SimplifyAsync] 处理普通 Async 方法");
				SimplifyAsyncWrapperBlocks(blocks);
			}
			else {
				Logger.v("非 MoveNext 或 Async 方法，跳过简化");
			}

			Logger.v("========== 异步状态机初始化简化结束 ==========\n");
		}

		/// <summary>
		/// 处理普通 Async 方法的初始化简化
		/// 这是原来的 SimplifyBlocksInPlace 逻辑
		/// </summary>
		private void SimplifyAsyncWrapperBlocks(Blocks blocks) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			Logger.v("开始简化异步包装方法初始化...");

			// 阶段 1：识别初始化块序列
			var initSequence = new List<Block>();
			var currentIdx = 0;

			// 初始化块应该以 ldc + stloc 开始
			for (int i = 0; i < Math.Min(8, allBlocks.Count); i++) {
				var block = allBlocks[i];

				// 检查是否包含初始化指令特征
				var hasBuilderOp = block.Instructions.Any(instr =>
					instr.Instruction.OpCode == OpCodes.Stfld ||
					instr.Instruction.OpCode == OpCodes.Ldflda);

				var hasStateAssignment = block.Instructions.Any(instr =>
					instr.Instruction.OpCode == OpCodes.Stloc ||
					instr.Instruction.OpCode == OpCodes.Stloc_0 ||
					instr.Instruction.OpCode == OpCodes.Stloc_1 ||
					instr.Instruction.OpCode == OpCodes.Stloc_2 ||
					instr.Instruction.OpCode == OpCodes.Stloc_3 ||
					instr.Instruction.OpCode == OpCodes.Stloc_S);

				if (hasBuilderOp || hasStateAssignment) {
					initSequence.Add(block);
					currentIdx = i;
					Logger.v("[SimplifyAsync] 发现初始化块 {0}: {1} 条指令", i, block.Instructions.Count);
				}
				else {
					break; // 初始化序列中断
				}
			}

			if (initSequence.Count == 0) {
				Logger.v("未找到初始化块序列");
				return;
			}

			Logger.v("[SimplifyAsync] 发现初始化块序列，共 {0} 个块", initSequence.Count);

			// 阶段 2：识别虚假控制流块
			// 检查是否存在常量条件分支
			Block falseConditionBlock = null;
			Block switchBlock = null;
			Block pseudoReturnBlock = null;

			for (int i = currentIdx + 1; i < allBlocks.Count; i++) {
				var block = allBlocks[i];
				if (block.Instructions.Count == 0) continue;

				var lastInstr = block.Instructions[block.Instructions.Count - 1];
				var lastIL = lastInstr.Instruction;

				// 检查 beq 模式：ldloc X; ldc.i4 11; beq
				if ((lastIL.OpCode == OpCodes.Beq || lastIL.OpCode == OpCodes.Beq_S) &&
				    block.Instructions.Count >= 2) {
					var prevInstr = block.Instructions[block.Instructions.Count - 2];
					if (IsLoadConstant(prevInstr.Instruction, out int constVal) && constVal > 0) {
						falseConditionBlock = block;
						Logger.v("[SimplifyAsync] 发现常量条件块（beq）: Block {0}", i);
					}
				}

				// 检查 switch 模式
				if (lastIL.OpCode == OpCodes.Switch) {
					switchBlock = block;
					Logger.v("[SimplifyAsync] 发现 switch 块: Block {0}", i);
				}

				// 检查伪造返回：beq.s 992
				if ((lastIL.OpCode == OpCodes.Beq || lastIL.OpCode == OpCodes.Beq_S) &&
				    block.Instructions.Count >= 2) {
					var prevInstr = block.Instructions[block.Instructions.Count - 2];
					if (IsLoadConstant(prevInstr.Instruction, out int val) && val == 992) {
						pseudoReturnBlock = block;
						Logger.v("[SimplifyAsync] 发现伪造返回块: Block {0}", i);
					}
				}
			}

			// 阶段 3：如果识别出虚假控制流，执行简化
			if (falseConditionBlock != null || switchBlock != null || pseudoReturnBlock != null) {
				Logger.v("[SimplifyAsync] 检测到虚假控制流，准备简化");
				Logger.v("[SimplifyAsync] 虚假分支块数: {0}",
					(falseConditionBlock != null ? 1 : 0) +
					(switchBlock != null ? 1 : 0) +
					(pseudoReturnBlock != null ? 1 : 0));

				// 阶段 4：执行块修改
				SimplifyBlocksInPlace(blocks, initSequence, falseConditionBlock, switchBlock, pseudoReturnBlock);
			}
			else {
				Logger.v("[SimplifyAsync] 未检测到虚假控制流模式");
			}
		}

		/// <summary>
		/// 实际修改块结构，移除虚假控制流并线性化
		/// </summary>
		private void SimplifyBlocksInPlace(Blocks blocks, List<Block> initSequence,
			Block falseConditionBlock, Block switchBlock, Block pseudoReturnBlock) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();

			// 获取 Start 调用块（应该在初始化序列后）
			Block startBlock = null;
			Block returnBlock = null;
			int startBlockIdx = -1;
			int returnBlockIdx = -1;

			for (int i = 0; i < allBlocks.Count; i++) {
				var block = allBlocks[i];

				// 查找 AsyncTaskMethodBuilder.Start 调用
				var hasStart = block.Instructions.Any(instr =>
					instr.Instruction.OpCode == OpCodes.Call &&
					instr.Instruction.Operand is IMethod method &&
					method.Name == "Start");

				if (hasStart) {
					startBlock = block;
					startBlockIdx = i;
					Logger.v("[SimplifyAsync] 发现 Start 调用块: Block {0}", i);
				}

				// 查找返回块（ret 指令）
				if (block.Instructions.Any(instr => instr.Instruction.OpCode == OpCodes.Ret)) {
					returnBlock = block;
					returnBlockIdx = i;
					Logger.v("[SimplifyAsync] 发现返回块: Block {0}", i);
				}
			}

			if (startBlock == null || returnBlock == null) {
				Logger.v("[SimplifyAsync] 未找到必要的块（Start 或 Return）");
				return;
			}

			// 现在重建块的连接关系
			// 目标：初始化块序列 -> Start -> 返回块

			// 1. 将最后一个初始化块的 FallThrough 指向 Start 块
			if (initSequence.Count > 0) {
				var lastInitBlock = initSequence[initSequence.Count - 1];
				lastInitBlock.FallThrough = startBlock;
				Logger.v("[SimplifyAsync] 连接最后初始化块 -> Start 块");
			}

			// 2. 将 Start 块的 FallThrough 指向返回块
			startBlock.FallThrough = returnBlock;
			startBlock.Targets = null; // 移除任何分支
			Logger.v("[SimplifyAsync] 连接 Start 块 -> 返回块");

			// 3. 移除虚假块的所有指令（清空这些块）
			if (falseConditionBlock != null) {
				// Block 3: 常量条件分支 beq
				// 这个块的所有指令都应该被删除（ldloc local1, ldc.i4 11, beq.s）
				var instrsToRemove = new List<Instr>();
				foreach (var instr in falseConditionBlock.Instructions) {
					var il = instr.Instruction;
					// 删除所有分支指令和其操作数加载
					if (il.OpCode == OpCodes.Beq || il.OpCode == OpCodes.Beq_S ||
					    il.OpCode == OpCodes.Brtrue || il.OpCode == OpCodes.Brtrue_S ||
					    il.OpCode == OpCodes.Brfalse || il.OpCode == OpCodes.Brfalse_S) {
						instrsToRemove.Add(instr);
						Logger.v("[SimplifyAsync] 删除分支指令: {0}", il.OpCode.Name);
					}
				}

				// 删除用于分支条件的加载常量
				if (instrsToRemove.Count > 0 && falseConditionBlock.Instructions.Count >= 2) {
					var secondLast = falseConditionBlock.Instructions[falseConditionBlock.Instructions.Count - 2];
					if (IsLoadConstant(secondLast.Instruction, out int _)) {
						instrsToRemove.Add(secondLast);
						Logger.v("[SimplifyAsync] 删除条件常量加载");
					}
				}

				// 删除加载状态变量的指令
				if (falseConditionBlock.Instructions.Count >= 3) {
					var thirdLast = falseConditionBlock.Instructions[falseConditionBlock.Instructions.Count - 3];
					if (IsLoadInstr(thirdLast.Instruction, out int _)) {
						instrsToRemove.Add(thirdLast);
						Logger.v("[SimplifyAsync] 删除状态变量加载");
					}
				}

				foreach (var instr in instrsToRemove) {
					falseConditionBlock.Instructions.Remove(instr);
				}

				falseConditionBlock.FallThrough = falseConditionBlock.FallThrough ?? startBlock;
				falseConditionBlock.Targets = null;
				Logger.v("[SimplifyAsync] 简化常量条件块");
			}

			// 4. 清空虚假 switch 块（它总是跳来跳去）
			if (switchBlock != null) {
				// Block 8: switch 指令和加载状态变量
				var switchInstrsToRemove = new List<Instr>();
				foreach (var instr in switchBlock.Instructions) {
					var il = instr.Instruction;
					if (il.OpCode == OpCodes.Switch) {
						switchInstrsToRemove.Add(instr);
						Logger.v("[SimplifyAsync] 删除 switch 指令");
					}
				}

				// 删除 switch 前面的加载指令
				if (switchInstrsToRemove.Count > 0 && switchBlock.Instructions.Count >= 2) {
					var beforeSwitch = switchBlock.Instructions[switchBlock.Instructions.Count - 2];
					if (IsLoadInstr(beforeSwitch.Instruction, out int _)) {
						switchInstrsToRemove.Add(beforeSwitch);
						Logger.v("[SimplifyAsync] 删除 switch 前的加载指令");
					}
				}

				foreach (var instr in switchInstrsToRemove) {
					switchBlock.Instructions.Remove(instr);
				}

				switchBlock.FallThrough = returnBlock;
				switchBlock.Targets = null;
				Logger.v("[SimplifyAsync] 简化虚假 switch 块");
			}

			// 5. 清空伪造返回块的分支指令
			if (pseudoReturnBlock != null) {
				// Block 10: beq.s 992 和相关指令
				var pseudoInstrsToRemove = new List<Instr>();
				foreach (var instr in pseudoReturnBlock.Instructions) {
					var il = instr.Instruction;
					if (il.OpCode == OpCodes.Beq || il.OpCode == OpCodes.Beq_S) {
						pseudoInstrsToRemove.Add(instr);
						Logger.v("[SimplifyAsync] 删除伪造返回分支指令");
					}
				}

				// 删除 beq 前的常量加载
				if (pseudoInstrsToRemove.Count > 0 && pseudoReturnBlock.Instructions.Count >= 2) {
					var beforeBeq = pseudoReturnBlock.Instructions[pseudoReturnBlock.Instructions.Count - 2];
					if (IsLoadConstant(beforeBeq.Instruction, out int _)) {
						pseudoInstrsToRemove.Add(beforeBeq);
						Logger.v("[SimplifyAsync] 删除伪造返回常量加载");
					}
				}

				// 删除状态变量加载
				if (pseudoReturnBlock.Instructions.Count >= 3) {
					var thirdLast = pseudoReturnBlock.Instructions[pseudoReturnBlock.Instructions.Count - 3];
					if (IsLoadInstr(thirdLast.Instruction, out int _)) {
						pseudoInstrsToRemove.Add(thirdLast);
						Logger.v("[SimplifyAsync] 删除伪造返回状态变量加载");
					}
				}

				foreach (var instr in pseudoInstrsToRemove) {
					pseudoReturnBlock.Instructions.Remove(instr);
				}

				pseudoReturnBlock.FallThrough = returnBlock;
				pseudoReturnBlock.Targets = null;
				Logger.v("[SimplifyAsync] 简化伪造返回块");
			}

			Logger.v("[SimplifyAsync] 块重建完成");

			// 阶段 5：合并空块
			MergeEmptyBlocks(blocks);
		}

		/// <summary>
		/// 合并空块：如果块没有指令，重定向其前驱指向其后继
		/// </summary>
		private void MergeEmptyBlocks(Blocks blocks) {
			var allBlocks = blocks.MethodBlocks.GetAllBlocks();
			Logger.v("[MergeEmpty] 开始合并空块...");

			bool changed = true;
			int iterations = 0;
			const int maxIterations = 10; // 防止无限循环

			while (changed && iterations < maxIterations) {
				changed = false;
				iterations++;

				for (int i = 0; i < allBlocks.Count; i++) {
					var block = allBlocks[i];

					// 跳过非空块和入口块
					if (block.Instructions.Count > 0 || i == 0) {
						continue;
					}

					// 空块：找出指向它的所有块并重定向
					var emptyBlockTarget = block.FallThrough; // 空块应该只有 FallThrough

					if (emptyBlockTarget == null) {
						continue; // 没有后继，跳过
					}

					// 找所有指向这个空块的块
					foreach (var otherBlock in allBlocks) {
						if (otherBlock == block) continue;

						// 检查 FallThrough
						if (otherBlock.FallThrough == block) {
							otherBlock.FallThrough = emptyBlockTarget;
							Logger.v("[MergeEmpty] 重定向 FallThrough: {0} -> {1}",
								otherBlock.GetHashCode(), emptyBlockTarget.GetHashCode());
							changed = true;
						}

						// 检查 Targets
						if (otherBlock.Targets != null && otherBlock.Targets.Contains(block)) {
							otherBlock.Targets.Remove(block);
							otherBlock.Targets.Add(emptyBlockTarget);
							Logger.v("[MergeEmpty] 重定向 Target: {0} -> {1}",
								otherBlock.GetHashCode(), emptyBlockTarget.GetHashCode());
							changed = true;
						}
					}
				}
			}

			Logger.v("[MergeEmpty] 合并完成，迭代次数: {0}", iterations);
		}

		/// <summary>
		/// 检查方法是否为异步方法（包括编译器生成的 MoveNext）
		/// 识别方式：
		/// 1. 方法返回类型为 Task 或 Task<T>
		/// 2. 方法名为 MoveNext 或包含 "Async" 后缀
		/// 3. 方法包含 AsyncTaskMethodBuilder 相关调用
		/// </summary>
		private bool IsAsyncMethod(MethodDef method) {
			if (method == null) return false;

			// 检查方法名：MoveNext（编译器生成的状态机方法）
			if (method.Name == "MoveNext") {
				return true;
			}

			// 检查方法名：以 Async 结尾
			if (method.Name.EndsWith("Async", System.StringComparison.Ordinal)) {
				return true;
			}

			// 检查返回类型是否为 Task 或 Task<T>
			var retType = method.ReturnType;
			if (retType != null) {
				var retTypeName = retType.FullName;
				if (retTypeName.StartsWith("System.Threading.Tasks.Task", System.StringComparison.Ordinal)) {
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// 检查指令是否为加载常量 (ldc.i4.X, ldc.i4 X, ldc.i4.s X)
		/// </summary>
		private bool IsLoadConstant(Instruction instr, out int value) {
			value = 0;
			if (instr.OpCode == OpCodes.Ldc_I4_0) {
				value = 0;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_1) {
				value = 1;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_2) {
				value = 2;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_3) {
				value = 3;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_4) {
				value = 4;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_5) {
				value = 5;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_6) {
				value = 6;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_7) {
				value = 7;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_8) {
				value = 8;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4_M1) {
				value = -1;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldc_I4) {
				if (instr.Operand is int intVal) {
					value = intVal;
					return true;
				}
			}

			if (instr.OpCode == OpCodes.Ldc_I4_S) {
				if (instr.Operand is sbyte sbyteVal) {
					value = sbyteVal;
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// 检查指令是否为存储到局部变量 (stloc, stloc.0-3, stloc.s)
		/// </summary>
		private bool IsStloc(Instruction instr, out int localIndex) {
			localIndex = -1;
			if (instr.OpCode == OpCodes.Stloc_0) {
				localIndex = 0;
				return true;
			}

			if (instr.OpCode == OpCodes.Stloc_1) {
				localIndex = 1;
				return true;
			}

			if (instr.OpCode == OpCodes.Stloc_2) {
				localIndex = 2;
				return true;
			}

			if (instr.OpCode == OpCodes.Stloc_3) {
				localIndex = 3;
				return true;
			}

			if (instr.OpCode == OpCodes.Stloc) {
				if (instr.Operand is Local local) {
					localIndex = local.Index;
					return true;
				}
			}

			if (instr.OpCode == OpCodes.Stloc_S) {
				if (instr.Operand is Local local) {
					localIndex = local.Index;
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// 检查指令是否为加载局部变量 (ldloc, ldloc.0-3, ldloc.s)
		/// </summary>
		private bool IsLoadInstr(Instruction instr, out int localIndex) {
			localIndex = -1;
			if (instr.OpCode == OpCodes.Ldloc_0) {
				localIndex = 0;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldloc_1) {
				localIndex = 1;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldloc_2) {
				localIndex = 2;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldloc_3) {
				localIndex = 3;
				return true;
			}

			if (instr.OpCode == OpCodes.Ldloc) {
				if (instr.Operand is Local local) {
					localIndex = local.Index;
					return true;
				}
			}

			if (instr.OpCode == OpCodes.Ldloc_S) {
				if (instr.Operand is Local local) {
					localIndex = local.Index;
					return true;
				}
			}

			return false;
		}

		/// <summary>
		/// 获取指令中加载的局部变量索引
		/// </summary>
		private int GetLocalFromLoadInstr(Instruction instr) {
			if (IsLoadInstr(instr, out int idx)) {
				return idx;
			}

			return -1;
		}

		/// <summary>
		/// 格式化单条指令为可读的文本
		/// </summary>
		string FormatInstruction(Instruction instr) {
			var sb = new System.Text.StringBuilder();
			sb.Append(instr.OpCode.Name);

			if (instr.Operand != null) {
				sb.Append(" ");

				// 处理不同的操作数类型
				if (instr.Operand is Instruction targetInstr) {
					sb.AppendFormat("IL_{0:X4}", targetInstr.Offset);
				}
				else if (instr.Operand is IList<Instruction> targets) {
					// switch 的多个目标
					sb.AppendFormat("({0})", string.Join(", ",
						targets.Select(t => $"IL_{t.Offset:X4}")));
				}
				else if (instr.Operand is Local local) {
					sb.AppendFormat("{0} ({1})", local.Name ?? "local", local.Index);
				}
				else if (instr.Operand is IField field) {
					sb.AppendFormat("{0}::{1}", field.DeclaringType?.Name ?? "?", field.Name);
				}
				else if (instr.Operand is IMethod methodRef) {
					sb.AppendFormat("{0}::{1}", methodRef.DeclaringType?.Name ?? "?", methodRef.Name);
				}
				else if (instr.Operand is int intVal) {
					sb.AppendFormat("{0}", intVal);
				}
				else if (instr.Operand is long longVal) {
					sb.AppendFormat("{0}", longVal);
				}
				else if (instr.Operand is sbyte sbyteVal) {
					sb.AppendFormat("{0}", sbyteVal);
				}
				else {
					sb.AppendFormat("{0}", instr.Operand?.ToString() ?? "null");
				}
			}

			return sb.ToString();
		}
	}
}

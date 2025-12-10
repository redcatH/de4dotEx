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
using de4dot.blocks.cflow;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.FlowCleaner {
	/// <summary>
	/// 异步状态机 MoveNext 方法的控制流去混淆器
	/// 处理 async/await 生成的 MoveNext 方法中的控制流扰乱
	/// 利用现有的 SwitchCflowDeobfuscator 和 BlockCflowDeobfuscator 进行处理
	/// </summary>
	public class AsyncMoveNextDeobfuscator : IBlocksDeobfuscator {
		Blocks? blocks;
		SwitchCflowDeobfuscator? switchDeobfuscator;
		BlockCflowDeobfuscator? blockDeobfuscator;
		int deobfuscateCount = 0;
		bool isAsyncMethod = false;

		public bool ExecuteIfNotModified { get; set; } = false;

		public void DeobfuscateBegin(Blocks blocks) {
			this.blocks = blocks;
			this.deobfuscateCount = 0;
			
			// 初始化内部去混淆器
			switchDeobfuscator = new SwitchCflowDeobfuscator();
			switchDeobfuscator.DeobfuscateBegin(blocks);
			
			blockDeobfuscator = new BlockCflowDeobfuscator();
			blockDeobfuscator.DeobfuscateBegin(blocks);

			// 检测异步方法特征
			isAsyncMethod = DetectAsyncMethod(blocks.Method);
			if (isAsyncMethod) {
				Console.WriteLine("[AsyncMoveNextDeobfuscator] 检测到异步方法: {0}.{1}", 
					blocks.Method.DeclaringType?.Name, blocks.Method.Name);
			}
		}

		public bool Deobfuscate(List<Block> allBlocks) {
			// 只处理异步 MoveNext 方法
			if (blocks?.Method.Name != "MoveNext" || !isAsyncMethod) {
				return false;
			}

			deobfuscateCount++;
			Console.WriteLine("[AsyncMoveNextDeobfuscator] Deobfuscate 执行 #{0}，块数: {1}", 
				deobfuscateCount, allBlocks.Count);

			bool modified = false;

			// 先应用 SwitchCflowDeobfuscator 处理 switch 语句重建
			if (switchDeobfuscator != null) {
				Console.WriteLine("[AsyncMoveNextDeobfuscator]   - 应用 SwitchCflowDeobfuscator");
				bool switchModified = switchDeobfuscator.Deobfuscate(allBlocks);
				if (switchModified) {
					Console.WriteLine("[AsyncMoveNextDeobfuscator]     ✓ SwitchCflowDeobfuscator 修改了代码");
				}
				modified |= switchModified;
			}

			// 然后应用 BlockCflowDeobfuscator 处理条件分支化简
			if (blockDeobfuscator != null) {
				Console.WriteLine("[AsyncMoveNextDeobfuscator]   - 应用 BlockCflowDeobfuscator");
				bool blockModified = blockDeobfuscator.Deobfuscate(allBlocks);
				if (blockModified) {
					Console.WriteLine("[AsyncMoveNextDeobfuscator]     ✓ BlockCflowDeobfuscator 修改了代码");
				}
				modified |= blockModified;
			}

			if (!modified) {
				Console.WriteLine("[AsyncMoveNextDeobfuscator] 本次执行没有修改代码");
			}

			return modified;
		}

		/// <summary>
		/// 检测方法是否为异步方法
		/// 查找特定的异步方法指示符
		/// </summary>
		private static bool DetectAsyncMethod(MethodDef method) {
			if (method?.Body == null) {
				return false;
			}

			var instrs = method.Body.Instructions;
			if (instrs.Count == 0) {
				return false;
			}

			// 检查字段名是否包含异步状态机标志
			var methodBody = method.Body;
			var locals = methodBody.Variables;
			
			// 检查本地变量名中是否有 <>1__state（异步状态机标志）
			bool hasStateMachineField = locals.Any(v => v.Name.Contains("__state")) ||
										instrs.Any(i => CheckAsyncIndicator(i));

			// 如果找到了异步指示符，直接返回
			if (hasStateMachineField) {
				return true;
			}

			// 备用检查：查找异步相关的方法调用
			foreach (var instr in instrs) {
				if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt) {
					var md = instr.Operand as IMethod;
					if (md != null) {
						string methodName = md.Name.String ?? "";
						// 检查异步相关的方法名
						if (methodName.Contains("AwaitUnsafeOnCompleted") ||
							methodName.Contains("SetStateMachine") ||
							methodName.Contains("AwaitOnCompleted") ||
							methodName.Contains("MoveNext")) {
							return true;
						}
					}
				}
			}

			return false;
		}

		/// <summary>
		/// 检查单个指令是否包含异步指示符
		/// </summary>
		private static bool CheckAsyncIndicator(Instruction instr) {
			if (instr.OpCode == OpCodes.Ldsfld || instr.OpCode == OpCodes.Ldfld || 
				instr.OpCode == OpCodes.Stfld) {
				var field = instr.Operand as IField;
				if (field != null) {
					string fieldName = field.Name.String ?? "";
					// 检查异步状态机的特定字段
					if (fieldName.Contains("__state") ||
						fieldName.Contains("__builder") ||
						fieldName.Contains("__awaiter") ||
						fieldName.Contains("__this")) {
						return true;
					}
				}
			}

			if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt) {
				var method = instr.Operand as IMethod;
				if (method != null) {
					string methodName = method.Name.String ?? "";
					if (methodName.Contains("AwaitUnsafeOnCompleted") ||
						methodName.Contains("SetStateMachine") ||
						methodName.Contains("AwaitOnCompleted")) {
						return true;
					}
				}
			}

			return false;
		}
	}
}

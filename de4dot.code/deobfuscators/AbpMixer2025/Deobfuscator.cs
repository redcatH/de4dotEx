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
using de4dot.code;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.AbpMixer2025;

public class Deobfuscator : DeobfuscatorBase {
	string _obfuscatorName = "Abp Obfuscator";

	// 用于存储检测到的不透明谓词字段
	public readonly List<FieldDef> OpaquePredicateFields = [];
	public readonly Dictionary<FieldDef, int?> FieldValues;
	public int ReplacedCount { get; set; }

	internal class Options : DeobfuscatorBase.OptionsBase {
	}

	public override void DeobfuscateEnd() {
		// 第一阶段：全局块分析（在所有块优化完成后）
		var analyzer = new MethodBlocksAnalyzer(this);
		foreach (var type in module.GetTypes()) {
			foreach (var method in type.Methods) {
				if (method.HasBody && method.Body.HasInstructions) {
					analyzer.AnalyzeMethod(method);
				}
			}
		}
		// 第二阶段：字段引用诊断
		if (!Logger.Instance.IgnoresEvent(LoggerEvent.Verbose)) {
			// 快速扫描：仅检查包含感兴趣 opcode 的方法
			var interestedOps = new HashSet<OpCode> {
				OpCodes.Ldfld,
				OpCodes.Ldflda,
				OpCodes.Ldind_I4,
				OpCodes.Ldind_I1,
				OpCodes.Ldind_U1,
				OpCodes.Ldind_I2,
				OpCodes.Ldind_U2,
				OpCodes.Call,
				OpCodes.Callvirt,
				OpCodes.Ldsfld
			};

			var occurrences = new List<string>();
			int totalFound = 0;

			// 遍历模块中所有类型/方法，查找对 opaquePredicateFields 的引用
			foreach (var t in module.GetTypes()) {
				foreach (var m in t.Methods) {
					if (m.Body == null || m.Body.Instructions == null)
						continue;

					var instrs = m.Body.Instructions;

					// 快速过滤：检查方法体是否包含感兴趣的 opcode
					if (!instrs.Any(ins => interestedOps.Contains(ins.OpCode)))
						continue;

					// 模式1: ldfld
					for (int i = 0; i < instrs.Count; i++) {
						var ins = instrs[i];
						if (ins.OpCode == OpCodes.Ldfld) {
							var targetField = ResolveTargetField(ins.Operand);
							if (targetField != null && OpaquePredicateFields.Contains(targetField)) {
								totalFound++;
								string moduleInstName = null;
								if (i > 0 && instrs[i - 1].OpCode == OpCodes.Ldsfld) {
									var inst = ResolveTargetField(instrs[i - 1].Operand);
									moduleInstName = inst != null ? inst.Name : "-";
								}

								var nextOp = (i + 1 < instrs.Count) ? instrs[i + 1].OpCode.Code.ToString() : "<end>";
								FieldValues.TryGetValue(targetField, out int? val1);
								occurrences.Add(string.Format(
									"Pattern1: {0}::{1} (IL#{2}) ldfld={3} -> value={4} next={5}",
									t.FullName, m.Name, i, targetField.Name,
									val1.HasValue ? val1.Value.ToString() : "null", nextOp));
							}
						}

						// 模式2: ldsfld; ldflda; ldind.*
						if (ins.OpCode == OpCodes.Ldsfld && i + 2 < instrs.Count) {
							var n1 = instrs[i + 1];
							var n2 = instrs[i + 2];
							if (n1.OpCode == OpCodes.Ldflda && (n2.OpCode == OpCodes.Ldind_I4 ||
							                                    n2.OpCode == OpCodes.Ldind_I1 ||
							                                    n2.OpCode == OpCodes.Ldind_U1 ||
							                                    n2.OpCode == OpCodes.Ldind_I2 ||
							                                    n2.OpCode == OpCodes.Ldind_U2)) {
								var targetField = ResolveTargetField(n1.Operand);
								if (targetField != null && OpaquePredicateFields.Contains(targetField)) {
									totalFound++;
									FieldValues.TryGetValue(targetField, out int? val2);
									occurrences.Add(string.Format(
										"Pattern2: {0}::{1} (IL#{2}) ldflda={3} ldind={4} -> value={5}",
										t.FullName, m.Name, i + 1, targetField.Name,
										n2.OpCode.Code, val2.HasValue ? val2.Value.ToString() : "null"));
								}
							}
						}

						// 模式3: call/callvirt 简单 getter
						if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is IMethod) {
							var md = (ins.Operand as IMethod).ResolveMethodDef();
							if (md != null && md.Body != null && md.Body.Instructions != null) {
								var bodyInstrs = md.Body.Instructions;
								int bi = 0;
								while (bi < bodyInstrs.Count && bodyInstrs[bi].OpCode == OpCodes.Nop) bi++;
								if (bi + 2 < bodyInstrs.Count && bodyInstrs[bi].OpCode == OpCodes.Ldsfld &&
								    bodyInstrs[bi + 1].OpCode == OpCodes.Ldfld &&
								    bodyInstrs[bi + 2].OpCode == OpCodes.Ret) {
									var targetField = ResolveTargetField(bodyInstrs[bi + 1].Operand);
									if (targetField != null && OpaquePredicateFields.Contains(targetField)) {
										totalFound++;
										FieldValues.TryGetValue(targetField, out int? val3);
										occurrences.Add(string.Format(
											"Pattern3: {0}::{1} (IL#{2}) call getter {3} -> field={4} value={5}",
											t.FullName, m.Name, i, md.FullName, targetField.Name,
											val3.HasValue ? val3.Value.ToString() : "null"));
									}
								}
							}
						}
					}
				}
			}

			// 输出汇总
			if (totalFound > 0) {
				foreach (var s in occurrences) {
					Logger.vv("[EndDiagnostics]   {0}", s);
				}

				Logger.v("[EndDiagnostics] 发现 {0} 处可能的字段引用", totalFound);
			}
			else {
				Logger.v("[EndDiagnostics] 未发现对不透明谓词字段的引用");
			}

			Logger.v("[EndDiagnostics] 共替换不透明谓词字段：{0}", ReplacedCount);
		}


		// 调用基类以执行后续的清理/删除逻辑
		base.DeobfuscateEnd();
	}

	/// <summary>
	/// 把各种 operand（FieldDef, FieldRef/MemberRef, IField）解析为 FieldDef（若能找到）
	/// </summary>
	public FieldDef ResolveTargetField(object operand) {
		if (operand == null) {
			return null;
		}

		// 优先尝试直接转换为 FieldDef
		var fd = operand as FieldDef;
		if (fd != null) {
			return fd;
		}

		// 尝试作为 IField 并 Resolve
		var ifield = operand as IField;
		if (ifield != null) {
			var resolved = ifield.ResolveFieldDef();
			if (resolved != null) {
				return resolved;
			}

			// 若 Resolve 失败，按 FullName 匹配
			var fullName = ifield.FullName;
			foreach (var k in FieldValues.Keys) {
				if (k.FullName == fullName) {
					return k;
				}
			}
		}

		switch (operand) {
		// MemberRef 回退
		case MemberRef mr: {
			var mrFullName = mr.FullName;
			foreach (var k in FieldValues.Keys.Where(k => k.FullName == mrFullName)) {
				return k;
			}

			break;
		}
		// 作为后备：按 fullname 在已知字段中查找（保守匹配）
		case string s:
			return FieldValues.Keys.FirstOrDefault(k => k.FullName == s);
		}

		return null;
	}

	static int? EvaluateInt32FromInstructions(IList<Instruction> instrs) {
		var stack = new System.Collections.Generic.List<int>();
		foreach (var instr in instrs) {
			switch (instr.OpCode.Code) {
			case Code.Ldc_I4_M1:
				stack.Add(-1); break;
			case Code.Ldc_I4_0:
				stack.Add(0); break;
			case Code.Ldc_I4_1:
				stack.Add(1); break;
			case Code.Ldc_I4_2:
				stack.Add(2); break;
			case Code.Ldc_I4_3:
				stack.Add(3); break;
			case Code.Ldc_I4_4:
				stack.Add(4); break;
			case Code.Ldc_I4_5:
				stack.Add(5); break;
			case Code.Ldc_I4_6:
				stack.Add(6); break;
			case Code.Ldc_I4_7:
				stack.Add(7); break;
			case Code.Ldc_I4_8:
				stack.Add(8); break;
			case Code.Ldc_I4:
			case Code.Ldc_I4_S:
				if (instr.Operand is int v)
					stack.Add(v);
				else
					return null;
				break;
			case Code.Neg:
				if (stack.Count < 1) return null;
				int v1 = stack[stack.Count - 1];
				stack.RemoveAt(stack.Count - 1);
				stack.Add(-v1);
				break;
			case Code.Not:
				if (stack.Count < 1) return null;
				int vNot = stack[stack.Count - 1];
				stack.RemoveAt(stack.Count - 1);
				stack.Add(~vNot);
				break;
			case Code.Add:
			case Code.Sub:
			case Code.Mul:
			case Code.Div:
			case Code.And:
			case Code.Or:
			case Code.Xor:
			case Code.Shl:
			case Code.Shr:
			case Code.Shr_Un:
				if (stack.Count < 2) return null;
				int b = stack[stack.Count - 1];
				stack.RemoveAt(stack.Count - 1);
				int a = stack[stack.Count - 1];
				stack.RemoveAt(stack.Count - 1);
				switch (instr.OpCode.Code) {
				case Code.Add: stack.Add(a + b); break;
				case Code.Sub: stack.Add(a - b); break;
				case Code.Mul: stack.Add(a * b); break;
				case Code.Div:
					if (b == 0) return null;
					stack.Add(a / b);
					break;
				case Code.And: stack.Add(a & b); break;
				case Code.Or: stack.Add(a | b); break;
				case Code.Xor: stack.Add(a ^ b); break;
				case Code.Shl: stack.Add(a << (b & 0x1F)); break;
				case Code.Shr: stack.Add(a >> (b & 0x1F)); break;
				case Code.Shr_Un: stack.Add((int)((uint)a >> (b & 0x1F))); break;
				}

				break;
			case Code.Conv_I4:
				// no-op for our purposes
				break;
			default:
				// unsupported opcode in expression
				break;
			}
		}

		if (stack.Count > 0)
			return stack[stack.Count - 1];
		return null;
	}

	public override IEnumerable<IBlocksDeobfuscator> BlocksDeobfuscators {
		get {
			var list = new List<IBlocksDeobfuscator>();
			// 优先处理异步状态机的 MoveNext 方法控制流扰乱
			// list.Add(new AsyncMoveNextDeobfuscator());
			// 然后做字段内联，再做保守块简化器，这样 emulator 能看到常量并折叠分支
			list.Add(new FieldInliner(this));
			foreach (var bd in base.BlocksDeobfuscators)
				list.Add(bd);
	
			return list;
		}
	}

	public override string Type => DeobfuscatorInfo.THE_TYPE;
	public override string TypeLong => DeobfuscatorInfo.THE_NAME;
	public override string Name => _obfuscatorName;
	Options _options;

	internal Deobfuscator(Options options)
		: base(options) {
		FieldValues = new Dictionary<FieldDef, int?>();
		this._options = options;
	}

	protected override int DetectInternal() {
		int score = 0;
		// return score;
		// 检测不透明谓词字段的数量
		if (OpaquePredicateFields.Count > 0) {
			score += OpaquePredicateFields.Count * 10;
			_obfuscatorName = $"{_obfuscatorName} (Opaque Predicates)";
		}

		return score;
	}

	public override IEnumerable<int> GetStringDecrypterMethods() => new List<int>();

	protected override void ScanForObfuscator() {
		var moduleType = DotNetUtils.GetModuleType(module);

		Logger.v("[ScanForObfuscator] 开始扫描 <Module> 类型中的字段");

		foreach (var t in module.GetTypes()) {
			var n = t.Name.String;
			if (n == null || !n.StartsWith("<Module", System.StringComparison.Ordinal))
				continue;
			if (t.Fields.Count < 3)
				continue;

			Logger.v("[ScanForObfuscator] 发现 <Module> 类型: {0}, 字段数: {1}", t.FullName, t.Fields.Count);

			// 查找静态实例字段（类型是当前 <Module> 类型本身）
			FieldDef staticInstanceField = null;
			foreach (var f in t.Fields) {
				if (f.IsStatic && f.FieldType != null && f.FieldType.FullName == t.FullName) {
					staticInstanceField = f;
					Logger.vv("[ScanForObfuscator] 找到静态实例字段: {0}", f.Name);
					break;
				}
			}

			if (staticInstanceField == null) {
				Logger.vv("[ScanForObfuscator] 未找到静态实例字段，跳过此类型");
				continue;
			}

			// 查找所有方法，寻找包含 stfld 指令的初始化方法
			MethodDef initMethod = null;
			foreach (var m in t.Methods) {
				if (m.Body == null || m.Body.Instructions == null)
					continue;

				// 检查方法中是否有对 staticInstanceField 的 stfld 操作
				bool hasStfldToInstance = false;
				int stfldCount = 0;
				foreach (var instr in m.Body.Instructions) {
					if (instr.OpCode == OpCodes.Stfld) {
						stfldCount++;
						var fld = instr.Operand as FieldDef;
						if (fld != null && fld.DeclaringType == t && fld.FieldType != null) {
							// 检查是否是对实例字段的赋值（类型是 int）
							if (fld.FieldType.FullName == "System.Int32") {
								hasStfldToInstance = true;
							}
						}
					}
				}

				// 如果方法包含大量 stfld（说明是初始化方法）
				if (hasStfldToInstance && stfldCount > 10) {
					initMethod = m;
					Logger.vv("[ScanForObfuscator] 找到初始化方法: {0}, stfld 数量: {1}", m.Name, stfldCount);
					break;
				}
			}

			if (initMethod == null) {
				Logger.vv("[ScanForObfuscator] 未找到初始化方法，跳过此类型");
				continue;
			}

			// 扫描初始化方法中的所有 stfld 指令
			var instrs = initMethod.Body.Instructions;
			Logger.vv("[ScanForObfuscator] 开始扫描初始化方法，指令数: {0}", instrs.Count);
			for (int i = 0; i < instrs.Count; i++) {
				var ins = instrs[i];
				if (ins.OpCode != OpCodes.Stfld)
					continue;

				var targetField = ins.Operand as FieldDef;
				if (targetField == null)
					continue;

				// 只处理 int 类型的字段
				if (targetField.FieldType == null || targetField.FieldType.FullName != "System.Int32")
					continue;

				// 向后搜索，查找 ldsfld staticInstanceField 和 value
				// 模式：... ldsfld <staticInstance> ... ldc.i4.x ... stfld <targetField>
				FieldDef foundStaticField = null;
				var valueInstructions = new List<Instruction>();

				// 简单向后搜索 20 条指令（足够覆盖常见的初始化模式）
				int searchStart = Math.Max(0, i - 20);
				for (int j = searchStart; j < i; j++) {
					var cur = instrs[j];

					// 找到 ldsfld staticInstanceField
					if (cur.OpCode == OpCodes.Ldsfld) {
						var fld = cur.Operand as FieldDef;
						if (fld == staticInstanceField) {
							foundStaticField = fld;
							valueInstructions.Clear(); // 重置，从 ldsfld 之后开始收集
							continue;
						}
					}

					// 在找到 staticInstanceField 之后，收集可能的值指令
					if (foundStaticField != null) {
						// 收集 ldc.i4.*, add, sub 等指令
						if (cur.OpCode.Code >= Code.Ldc_I4_M1 && cur.OpCode.Code <= Code.Ldc_I4_8 ||
						    cur.OpCode.Code == Code.Ldc_I4 || cur.OpCode.Code == Code.Ldc_I4_S ||
						    cur.OpCode.Code == Code.Add || cur.OpCode.Code == Code.Sub ||
						    cur.OpCode.Code == Code.Mul || cur.OpCode.Code == Code.Div ||
						    cur.OpCode.Code == Code.And || cur.OpCode.Code == Code.Or ||
						    cur.OpCode.Code == Code.Xor || cur.OpCode.Code == Code.Shl ||
						    cur.OpCode.Code == Code.Shr || cur.OpCode.Code == Code.Shr_Un ||
						    cur.OpCode.Code == Code.Neg || cur.OpCode.Code == Code.Not) {
							valueInstructions.Add(cur);
						}
					}
				}

				// 如果找到了 staticInstanceField，说明这是一个有效的字段初始化
				if (foundStaticField != null) {
					if (!OpaquePredicateFields.Contains(targetField))
						OpaquePredicateFields.Add(targetField);

					// 尝试计算值
					var val = EvaluateInt32FromInstructions(valueInstructions);
					FieldValues[targetField] = val;

					Logger.vv("[ScanForObfuscator]   -> 字段: {0} = {1}",
						targetField.Name, val.HasValue ? val.Value.ToString() : "null");
				}
			}
		}

		// 输出扫描结果摘要
		if (OpaquePredicateFields.Count > 0) {
			Logger.v("[ScanForObfuscator] ===== 扫描完成 =====");
			Logger.v("[ScanForObfuscator] 发现 {0} 个不透明谓词字段", OpaquePredicateFields.Count);
			int withValue = 0;
			int withoutValue = 0;
			foreach (var kvp in FieldValues) {
				if (kvp.Value.HasValue)
					withValue++;
				else
					withoutValue++;
			}

			Logger.vv("[ScanForObfuscator] 有值: {0}, 无值(null): {1}", withValue, withoutValue);
		}
		else {
			Logger.v("[ScanForObfuscator] 扫描完成: 没有发现不透明谓词字段");
		}
	}
}

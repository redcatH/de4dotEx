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
using de4dot.blocks;
using de4dot.blocks.cflow;
using de4dot.code;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace de4dot.code.deobfuscators.AbpMixer2025 {
	/// <summary>
	/// 字段内联器：把对模块实例字段的读取替换为常量（如果 fieldValues 有值）
	/// 注意：这个类只处理整数字段，不处理类型字段（如 typeof(T) 赋值）
	/// </summary>
	class FieldInliner : BlockDeobfuscator {
		readonly Deobfuscator _owner;

		internal FieldInliner(Deobfuscator owner) => this._owner = owner;

		// 委托给外层 owner 的 ResolveTargetField 方法
		FieldDef ResolveTargetField(object operand) {
			return _owner.ResolveTargetField(operand);
		}

		protected override bool Deobfuscate(Block block) {
			bool modified = false;
			var instrs = block.Instructions;
			int replacedCount = 0;

			for (int i = 0; i < instrs.Count; i++) {
				var ins = instrs[i].Instruction;

				// 模式1: ldsfld; ldfld
				if (ins.OpCode == OpCodes.Ldsfld && i + 1 < instrs.Count) {
					var next = instrs[i + 1].Instruction;
					if (next.OpCode == OpCodes.Ldfld) {
						var targetField = ResolveTargetField(next.Operand);
						if (targetField != null && _owner.FieldValues.TryGetValue(targetField, out int? val) &&
						    val.HasValue) {
							block.Replace(i, 2, Instruction.CreateLdcI4(val.Value));
							modified = true;
							replacedCount++;
							i--; // 调整索引，因为替换后指令数量变化
						}
					}
				}

				// 模式2: ldsfld; ldflda; ldind.*
				if (ins.OpCode == OpCodes.Ldsfld && i + 2 < instrs.Count) {
					var n1 = instrs[i + 1].Instruction;
					var n2 = instrs[i + 2].Instruction;
					if (n1.OpCode == OpCodes.Ldflda && (n2.OpCode == OpCodes.Ldind_I4 ||
					                                    n2.OpCode == OpCodes.Ldind_I1 ||
					                                    n2.OpCode == OpCodes.Ldind_U1 ||
					                                    n2.OpCode == OpCodes.Ldind_I2 ||
					                                    n2.OpCode == OpCodes.Ldind_U2)) {
						var targetField = ResolveTargetField(n1.Operand);
						if (targetField != null && _owner.FieldValues.TryGetValue(targetField, out int? val2) &&
						    val2.HasValue) {
							block.Replace(i, 3, Instruction.CreateLdcI4(val2.Value));
							modified = true;
							replacedCount++;
							i--;
						}
					}
				}

				// 模式3: call/callvirt 简单 getter 内联
				if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt) && ins.Operand is IMethod) {
					var md = (ins.Operand as IMethod).ResolveMethodDef();
					if (md != null && md.Body != null && md.Parameters.Count == 0) {
						var bodyInstrs = md.Body.Instructions;
						int bi = 0;
						while (bi < bodyInstrs.Count && bodyInstrs[bi].OpCode == OpCodes.Nop) bi++;
						if (bi + 1 < bodyInstrs.Count && bodyInstrs[bi].OpCode == OpCodes.Ldsfld) {
							// ldsfld; ldfld; ret
							if (bi + 2 < bodyInstrs.Count && bodyInstrs[bi + 1].OpCode == OpCodes.Ldfld &&
							    bodyInstrs[bi + 2].OpCode == OpCodes.Ret) {
								var targetField = ResolveTargetField(bodyInstrs[bi + 1].Operand);
								if (targetField != null &&
								    _owner.FieldValues.TryGetValue(targetField, out int? gv) && gv.HasValue) {
									block.Replace(i, 1, Instruction.CreateLdcI4(gv.Value));
									modified = true;
									replacedCount++;
									i--;
									continue;
								}
							}

							// ldsfld; ret
							if (bi + 1 < bodyInstrs.Count && bodyInstrs[bi + 1].OpCode == OpCodes.Ret) {
								var targetField = ResolveTargetField(bodyInstrs[bi].Operand);
								if (targetField != null &&
								    _owner.FieldValues.TryGetValue(targetField, out int? gv2) && gv2.HasValue) {
									block.Replace(i, 1, Instruction.CreateLdcI4(gv2.Value));
									modified = true;
									replacedCount++;
									i--;
									continue;
								}
							}
						}
					}
				}
			}

		if (replacedCount > 0) {
			_owner.ReplacedCount = _owner.ReplacedCount + replacedCount;
			Logger.v("[FieldInliner] 替换 {0} 处字段为常量", replacedCount);
		}			return modified;
		}
	}
}

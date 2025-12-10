# IL 代码输出指南

## 功能说明

`ConstructorCleaner` 类中的 `DumpMethodIL()` 方法可以输出完整的方法 IL 代码，帮助你分析控制流。

## 使用方法

### 1. 运行时启用输出

使用 `-vv` 命令行参数运行 de4dot：

```powershell
de4dot.exe -vv input.exe -o output.exe
```

或指定特定方法：

```powershell
de4dot.exe -vv input.exe -o output.exe | Select-String "IdentityUserIntegrationService"
```

### 2. 输出格式

```
========== 方法 IL 代码 ==========
方法: Volo.Abp.Identity.Integration.IdentityUserIntegrationService::FindByIdAsync
Block 数量: 5
局部变量数量: 2

--- Block 0 (Label: IL_0000) ---
  IL_0000: ldc.i4 3
  IL_0005: stloc 1
  IL_0009: ldloca.s 0
  IL_000b: call Create()
  → 目标: IL_0015

--- Block 1 (Label: IL_0015) ---
  IL_0015: ldc.i4 2
  IL_001a: stloc 1
  IL_001e: ldloca.s 0
  → 目标: IL_0026

========== 方法 IL 代码结束 ==========
```

## 输出内容说明

| 项目 | 说明 |
|------|------|
| `Block N` | 块的索引号 |
| `IL_XXXX` | 指令在方法体中的偏移量 |
| `OpCode Name` | IL 操作码名称（如 `ldc.i4`, `stloc` 等） |
| 操作数 | 指令的参数（变量名、字段名、跳转目标等） |
| `→ 目标` | 该块的后继块 |

## 关键信息

### 识别垃圾状态机变量

看输出中的这类模式：
```
IL_0000: ldc.i4 3        ; 加载常数
IL_0005: stloc 1         ; 存储到 local_1
IL_0009: ...
IL_002f: ldloc 1         ; 加载 local_1
IL_0033: ldc.i4 11       ; 加载常数
IL_0038: beq.s IL_003c   ; 条件分支
```

这表示 `local_1` 只被赋予常量，只用于条件分支 → **垃圾变量**

### 识别虚假 switch 分支

```
IL_005c: ldloc 1
IL_0060: switch (IL_0044, IL_0088, IL_001e, IL_0009, IL_004c)
IL_0079: br.s IL_002f
```

- `switch` 有 5 个分支目标
- 某些目标可能永不到达（取决于实际的状态值）

### 识别嵌套 while 循环

```
IL_007b: ldloc 1          ; 加载状态变量
IL_007f: ldc.i4 992       ; 加载常数
IL_0084: beq.s IL_005c    ; 如果等于则跳回 (loop entry)
IL_0086: br.s IL_0044     ; 否则跳到其他地方
```

- 从 `IL_005c` 到 `IL_007b` 形成一个循环
- 条件 `== 992` 控制循环继续

## 调试技巧

### 1. 只输出特定方法

修改 `DumpMethodIL()` 前的条件：

```csharp
if (!Logger.Instance.IgnoresEvent(LoggerEvent.VeryVerbose) &&
    method.Name == "FindByIdAsync") {
    DumpMethodIL(blocks);
}
```

### 2. 保存到文件

结合 PowerShell 重定向：

```powershell
de4dot.exe -vv input.exe -o output.exe 2>&1 | Tee-Object -FilePath analysis.log
```

然后在 `analysis.log` 中搜索：

```powershell
Select-String "========== 方法 IL 代码 ==========" analysis.log
```

### 3. 比较前后变化

在优化前后分别运行，对比输出：

```powershell
# 优化前
de4dot.exe -vv input.exe -o output1.exe 2>&1 | Select-String "IL_" > before.txt

# 优化后（修改代码后）
de4dot.exe -vv input.exe -o output2.exe 2>&1 | Select-String "IL_" > after.txt

# 比较
diff before.txt after.txt
```

## 格式化的操作数

### 本地变量
```
stloc local_1 (0)  ; 变量名和索引
ldloc local_2 (1)
```

### 字段引用
```
ldsfld MonoBlus::_instance   ; 静态字段
ldfld State::_value           ; 实例字段
```

### 方法调用
```
call MonoBlus::Initialize
callvirt IService::GetData
```

### 分支目标
```
beq.s IL_003c     ; 条件分支
switch (IL_0044, IL_0088, IL_001e)  ; switch 的所有目标
```

## 性能注意

- `DumpMethodIL()` 仅在 `-vv` 标志启用时执行
- 输出对大型方法可能会生成很多日志
- 考虑添加方法名过滤来减少输出量

## 下一步

有了 IL 代码后，你可以：

1. **识别垃圾变量** - 查找"仅被赋予常量，仅用于分支"的局部变量
2. **追踪状态转移** - 理解 switch 或 beq 如何控制执行流
3. **找出死代码** - 识别永不到达的块或分支
4. **优化策略** - 制定删除无用代码的计划

---

**提示**：打开 VS Code 的输出面板，使用 Ctrl+F 搜索 IL 偏移量，可以快速定位代码段。

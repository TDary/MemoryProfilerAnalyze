# MemoryProfilerAnalyze

基于 Unity Memory Profiler 的自动化内存快照解析工具。通过命令行方式加载 `.snap` 快照文件，解析后输出结构化的 CSV 数据，便于集成到 CI/CD 流程中进行内存回归分析。

## 环境要求

- Unity 2022

## 使用方式

通过 cmd 命令行运行 Unity，传入快照路径及输出文件路径：

```bash
Unity.exe -projectPath D:\MemoryProfilerAnalyze -quit -batchmode -nographics -executeMethod MemAnalyzer.Entrance.StartServer -logFile D:\files\AnalyzeTest.log -SnapFilePath D:\files\Test.snap -UUID test1111 -SummaryCsv D:\files\Summary.csv -UnityObjectsCsv D:\files\UnityObjects.csv -AllMemoryDataCsv D:\files\AllMemoryData.csv
```

> 路径参数可按实际环境替换。

## 命令行参数

| 参数 | 必填 | 说明 |
|------|------|------|
| `-SnapFilePath` | 是 | Memory Profiler 生成的 `.snap` 快照文件路径 |
| `-UUID` | 是 | 本次解析的唯一标识符 |
| `-SummaryCsv` | 是 | Summary 数据输出路径（CSV 格式） |
| `-UnityObjectsCsv` | 是 | Unity Objects 数据输出路径（CSV 格式） |
| `-AllMemoryDataCsv` | 是 | All Memory 数据输出路径（CSV 格式） |
| `-FunReferenceData` | 否 | 引用堆栈数据输出路径（已实现，暂未启用） |

## 输出文件说明

### Summary.csv

内存概要数据，包含以下分组：

- Allocated Memory Distribution — 内存分配分布
- Managed Heap Utilization — 托管堆使用情况
- Top Unity Objects Categories — 按 Category 统计的 Top Unity 对象
- Memory Usage On Device — 设备上的内存占用

| 列名 | 说明 |
|------|------|
| GroupName | 分组名称 |
| ResourceItemName | 资源项名称 |
| AllocatedSize | 已分配大小（字节） |

### UnityObjects.csv

Unity 对象明细数据。

| 列名 | 说明 |
|------|------|
| UnityObjectType | 对象类型 |
| ResourceItemName | 资源项名称 |
| InstanceId | 实例 ID |
| AllocatedSize | 总分配大小 |
| NativeSize | Native 内存大小 |
| ManagedSize | Managed 内存大小 |
| GraphicsSize | 显存大小 |

### AllMemoryData.csv

全量内存追踪数据，按以下分组输出：

| 分组 | 说明 |
|------|------|
| Managed | 托管内存（Reserved + Managed Objects） |
| Native | 原生内存（Managers / UnsafeUtility / Rendering 等） |
| Graphics (Estimated) | 估算显存（ComputeBuffers 等） |
| Executables & Mapped | 可执行文件与映射内存（DXCache / Other Dll） |
| Untracked* | 未追踪内存 |
| 其他 | 其余分类数据 |

| 列名 | 说明 |
|------|------|
| GroupName | 一级分组名称 |
| SubGroupName | 二级分组名称 |
| ResourceItemName | 资源项名称 |
| Size | 大小（字节） |

## 特性

- **超时保护**：快照加载超时 10 分钟自动退出，避免进程挂死
- **文件占用检测**：快照文件被占用时跳过解析并输出错误日志
- **文件大小校验**：快照文件为 0 字节时跳过解析（通常为截图失败）
- **引用堆栈追踪**：支持获取 Managed 对象的引用链（最大深度 5 层，多线程并行处理），暂未在命令行入口启用

## Editor 测试工具

项目中包含 Editor 窗口测试工具，可在 Unity Editor 中通过菜单 `Tools > MyTest` 打开，提供以下按钮：

| 按钮 | 功能 |
|------|------|
| Run Test | 加载指定路径的快照文件 |
| GenerateResultData | 导出 AllMemoryData JSON + 引用堆栈数据 |
| GenerateUnityObjectsData | 导出 UnityObjects CSV |
| GenerateAllData | 一键导出 Summary / UnityObjects / AllMemoryData 三个 CSV |
| UnloadSnap | 卸载快照并释放内存 |

## 项目结构

```
Assets/
├── Entrance.cs                          # 命令行入口，解析参数并启动分析
├── MemProfiler.cs                       # 分析流程控制（参数校验、加载、超时、输出）
├── Editor/
│   └── MyTest.cs                        # Editor 窗口测试工具
└── LitJson/                             # JSON 序列化库
Packages/
└── com.unity.memoryprofiler@1.1.3/
    └── Editor/
        └── MemoryProfilerNoWindow.cs    # 核心解析与数据导出逻辑
```

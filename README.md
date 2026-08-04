# 逆元渗透 InverseElementInfiltration

## 协作约定

### Assets 目录结构

```
Assets/
├── _Project/          # 业务代码（所有 .cs 源码都在这，按 asmdef 划分）
│   ├── Core/          # Core.asmdef：零依赖纯逻辑（不引用任何其他模块）
│   │   ├── Managers/  # 全局管理器（常驻，启动创建全程存在）：EventCenter.cs / AudioManager.cs / SaveManager.cs / RandomManager.cs / SettingsSystem.cs / UIManager.cs
│   │   ├── Utils/     # 工具类、扩展方法、单例基类、ReentrantGuard.cs（可重入锁）、Assert.cs（前置断言）
│   │   ├── Data/      # 通用数据结构：ScriptableObject 基类、通用类型、ISnapshot.cs（存档快照接口）
│   │   └── Input/     # 输入相关组件 / 动作映射（拖拽 / 点击 / 快捷键），不做全局输入管理器
│   ├── Data/          # Data.asmdef：纯数据零行为（架构数据层：模板族 / 行动族 / 请求族 / 定义配置族 / 枚举族 / ConfigTable）
│   │   └── Enums/     # 枚举族（Side、PieceType、Facing、Footprint、Direction、BattlePhase 等）
│   ├── Gameplay/      # Gameplay.asmdef：规则层（依赖 Core + Data）
│   │   ├── Battle/    # 战棋战斗
│   │   │   ├── Grid/      # 网格系统、坐标变换（逻辑坐标 ↔ 屏幕坐标，8×8 方格）
│   │   │   ├── Units/     # 单位：移动、攻击、技能
│   │   │   ├── AI/        # 敌方 AI（DecideTurn 产出请求，走统一执行管线）
│   │   │   ├── Effects/   # 战斗特效、伤害数字逻辑
│   │   │   └── TurnSystem/# 回合流程 / 行动点（AP，玩家自由选择棋子行动）
│   │   ├── Roguelike/ # 爬塔
│   │   │   ├── Map/       # 节点序列生成（单线：每层节点数量 / 类型排布）
│   │   │   ├── Run/       # 单局状态（卡组 / 遗物 / 积分）
│   │   │   └── Events/    # 随机事件（事件池加权抽取，EventNodeSystem 经 Resolver 落账）
│   │   └── 核心类直接放本目录（GameState.cs / BattleFlow.cs / TowerFlow.cs / BoardRules.cs / IntentResolver.cs / Resolver.cs / EventNodeSystem.cs / EditorSession.cs / EnemyAI.cs / FloorRules.cs / TutorialSystem.cs / ProgressSystem.cs / PieceInstance.cs）
│   ├── UI/            # UI.asmdef：UI 逻辑脚本（依赖 Core + Data + Gameplay）
│   │   ├── Views/     # 页面脚本（xxxPanel.cs，继承 PanelBase，实现 IPanel 注册进 UIManager）
│   │   ├── Widgets/   # 通用控件脚本（血条、按钮、弹窗）
│   │   └── Animations/# UI 动效（DOTween 封装层）
│   └── Editor/        # Editor.asmdef：编辑器工具（仅编辑器平台，不打包进游戏）
│       └── Tools/     # 自定义窗口、批量处理工具
├── Art/               # 美术资产（LFS）
│   ├── Characters/    # 单位立绘 / 动画
│   ├── Tiles/         # 地块贴图（斜 45° 图块）
│   ├── UI/            # UI 贴图 / 图标 / 页面 Prefab
│   ├── Effects/       # 特效序列帧 / 粒子材质
│   └── Environment/   # 背景 / 装饰
├── Audio/             # 音频（LFS）
│   ├── BGM/           # 背景音乐
│   └── SFX/           # 音效
├── Data/              # 数值 / 文案 JSON（可调，不重打包）
├── Fonts/             # 字体（子集化 SDF，动态字体兜底）
├── Scenes/            # 场景（单场景 + 面板切换，无场景切换需求）
└── Settings/          # 全局配置（ScriptableObject 实例，策划调数值用）
```

> 测试代码统一放 `Assets/Tests/`（独立 Tests asmdef，引用 Core、Data、Gameplay，框架 Unity Test Framework（NUnit））。

> 项目根 `docs/` 目录：存放设计文档、接口使用说明（如 UIManager 的 ShowPanel、HidePanel、PushPanel、PopPanel，EventCenter 事件注册），新增文档先看这里有没有同类。

### 技术栈

- Unity 2022.3.62f2c1：3D Built-In 模板 + 斜 45° 视角（3D 场景 + 2D 资产）
- asmdef × 4：Core / Data / Gameplay / UI，单向依赖 Core ← Data ← Gameplay ← UI（Editor 工具独立 Editor.asmdef，仅编辑器平台）
- Addressables：资源按需加载
- DOTween：动效
- Newtonsoft.Json：数据读写
- Input System：输入

### git
- 从 main 拉 `前缀/描述` 分支，PR 合入，禁止直推 main
- 合并一律使用 **Merge commit**（保留分支历史，提交图上可见分叉），禁止 fast-forward / squash / rebase 合并
- 纯文档/配置修改（README、docs/、.gitignore 等）可直接推 main
- 分支前缀（描述用英文小写+连字符，例：`feature/battle-grid`）：
  - `feature/` 新功能
  - `fix/` 修 bug
  - `docs/` 文档更新
  - `chore/` 配置/依赖/杂务
- 提交前缀 + 中文描述：
  - `feat:` 新功能
  - `fix:` 修 bug
  - `refactor:` 重构（行为不变）
  - `docs:` 文档更新
  - `chore:` 配置/依赖/杂务
  - `perf:` 性能优化

### 命名
- 禁止没有语义的命名，如纯数字、纯符号等

### 资源
- 美术/音频二进制走 LFS 自动处理，正常 add/commit 即可

### 字体
- 字体使用子集化 SDF，在 Assets/Fonts 中管理，动态字体兜底

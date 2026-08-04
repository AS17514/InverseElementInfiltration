# 逆元渗透 InverseElementInfiltration

## 协作约定

### Assets 目录结构

```
Assets/
├── _Project/          # 业务代码（所有 .cs 源码都在这，按 asmdef 划分）
│   ├── Core/          # Core.asmdef：零依赖纯逻辑（不引用任何其他模块）
│   │   ├── Managers/  # 全局管理器：EventCenter / AudioManager / SaveManager / SceneLoader / UIManager
│   │   ├── Utils/     # 工具类、扩展方法、单例基类、ReentrantGuard / Assert
│   │   ├── Data/      # 数据结构定义（ScriptableObject 基类、通用类型、ISnapshot）
│   │   └── Input/     # 输入抽象层（鼠标/键盘动作映射）
│   ├── Data/          # Data.asmdef：纯数据零行为（架构数据层）
│   │   ├── Templates/ # 模板族（MoveTemplate / AttackTemplate / SkipTemplate / TargetParam）
│   │   ├── Actions/   # 行动族（MoveAction / AttackAction / DeployAction / PromoteAction / SkipAction）
│   │   ├── Requests/  # 请求族（DeployRequest / PromoteRequest / ExecuteRequest）
│   │   ├── Definitions/# 定义配置族（PieceDef / PromotionConfig / FloorConfig / MapConfig / AIParams / 事件池）
│   │   ├── Enums/     # 枚举族（Side / PieceType / Facing / Footprint / Direction / BattlePhase / ...）
│   │   └── ConfigTable.cs # 配置加载（读 Assets/Data JSON）
│   ├── Gameplay/      # Gameplay.asmdef：规则层（依赖 Core + Data）
│   │   ├── Battle/    # 战棋战斗：Grid / Units / AI / Effects / TurnSystem
│   │   ├── Roguelike/ # 爬塔：Map / Run / Events
│   │   └── 核心类直接放本目录（GameState / BattleFlow / TowerFlow / BoardRules / IntentResolver / Resolver / EventNodeSystem / EditorSession / EnemyAI / FloorRules / TutorialSystem / ProgressSystem / PieceInstance）
│   ├── UI/            # UI.asmdef：UI 逻辑脚本（依赖 Core + Data + Gameplay）
│   │   ├── Views/     # 页面脚本（xxxPanel.cs，继承 PanelBase）
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
├── Fonts/             # 字体（子集化 SDF + 动态字体）
├── Scenes/            # 场景（Boot / MainMenu / Battle）
└── Settings/          # 全局配置（ScriptableObject 实例）
```

> 项目根 `docs/` 目录：存放设计文档、接口使用说明（如 UIManager 的 Open/Close、EventCenter 事件注册），新增文档先看这里有没有同类。

### 技术栈

- Unity 2022.3.62f2c1：3D Built-In 模板 + 斜 45° 视角（3D 场景 + 2D 资产）
- asmdef × 4：Core / Data / Gameplay / UI，单向依赖 Core ← Data ← Gameplay ← UI（Editor 工具独立 Editor.asmdef，仅编辑器平台）
- Addressables：资源按需加载
- DOTween：动效
- Newtonsoft.Json：数据读写
- Input System：输入

### git
- 从 main 拉 `前缀/描述` 分支，PR 合入，禁止直推 main
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

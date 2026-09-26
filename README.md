# Sephiria Mods

《Sephiria》自制 Mod 项目。

在游玩《Sephiria》的过程中，我尝试修改自己感兴趣的游戏机制，并借助
dnSpy、UnityExplorer、BepInEx、HarmonyX 和 AI
辅助完成代码定位、插件实现与测试。

目前整理了 3 个已经实际运行测试的 Mod。

## 项目内容

### 1. Full Item Pool Rewards

修改局内奖励选择机制。

原版奖励只会从随机候选中进行选择。我希望尝试让玩家能够从更完整的物品池中进行选择，因此制作了这一
Mod，并进一步处理了多人联机情况下的奖励同步。

主要实现：

-   使用 BepInEx 加载插件
-   使用 HarmonyX Patch 修改原游戏逻辑
-   筛选可用的 Charm / StoneTablet 奖励
-   复用原游戏 Journal UI 展示奖励池
-   支持连续选择 3 个奖励
-   使用 Mirror NetworkMessage 处理客户端选择与服务器发奖
-   完成单机、房主及非房主客户端测试

开发过程中遇到的主要问题之一，是客户端虽然能够正常打开选择界面，但非房主无法直接修改服务器权威的游戏状态。之后将流程调整为客户端提交选择结果，由服务器确认并执行奖励发放。

### 2. More Enemies Per Stage

修改关卡中的敌人生成数量。

主要实现：

-   定位敌人生成相关逻辑
-   在服务器侧修改 `MonsterSpawnData.count`
-   默认将敌人生成数量调整为原来的 2 倍
-   在游戏内测试实际生成效果

这个 Mod 主要用于尝试改变战斗密度，并熟悉从游戏逻辑定位到 Runtime Patch
的完整流程。

### 3. More Starting Talent

修改角色可使用的天赋点上限。

主要实现：

-   定位 `PlayerAvatar` 中相关字段
-   将 `maxPassivePoint` 从 40 调整为 60
-   通过 BepInEx / HarmonyX 应用修改
-   在游戏内验证结果

## 开发过程

这个项目最初不是为了制作一套完整的 Mod
框架，而是从实际游玩时产生的几个想法开始。

大致流程是：

1.  从想修改的游戏机制出发
2.  使用 dnSpy / UnityExplorer 定位相关类、字段和运行状态
3.  确定可以介入的逻辑位置
4.  使用 C#、BepInEx 和 HarmonyX 编写插件
5.  编译 DLL 并加载到游戏
6.  在实际游戏中验证修改结果
7.  根据报错、状态覆盖或联机不同步等问题继续调整

其中代码实现和技术方案探索大量使用了 AI
辅助。我主要负责提出修改目标、提供反编译与运行时上下文、判断方案是否符合预期，并进行集成、游戏内测试和迭代。

## 使用工具

-   C#
-   BepInEx
-   HarmonyX
-   Mirror
-   dnSpy
-   UnityExplorer
-   AssetStudio
-   Visual Studio

## Repository Structure

``` text
Sephiria-Mods/
├── FullItemPoolRewards/
├── MoreEnemiesPerStage/
└── MoreStartingTalent/
```

仓库仅保留本人编写和整理的 Mod
源码及项目文件，不包含《Sephiria》游戏本体、原版程序集、反编译源码或第三方工具文件。

## Demo

Bilibili：

https://www.bilibili.com/video/BV17uh26rE4Q/

## Status

个人学习与游戏机制实验项目。

代码来自实际测试版本的整理。由于游戏版本更新可能导致类、字段或 Patch
位置发生变化，当前仓库不保证适配后续所有游戏版本。

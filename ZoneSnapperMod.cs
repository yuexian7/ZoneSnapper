using System;
using Colossal.Localization;
using Colossal.Logging;
using Game;
using Game.Input;
using Game.Modding;
using Game.Rendering;
using Game.SceneFlow;
using Game.Tools;
using HarmonyLib;
using Unity.Entities;
using UnityEngine.InputSystem;
using UnityEngine;
using ZoneSnapper.Engine;
using ZoneSnapper.GameSide;
using ZoneSnapper.Patches;
using ZoneSnapper.Systems;

namespace ZoneSnapper
{
    /// <summary>
    /// Zone Snapper 模组入口。
    ///
    /// 【一句话原理】跟着游戏自带的区域绘制工具（AreaToolSystem），把它那张控制点表
    /// 换成「玩家手动节点 + 沿网络描边的自动节点」，玩家不需要任何额外按钮。
    ///
    /// 【本文件的顺序是硬约束】（Playbook §3.2）
    ///   LoadSettings 必须先于 RegisterKeyBindings —— 否则玩家改过的键会被特性的默认键顶掉。
    ///
    /// 【为什么带 Harmony】AreaToolSystem 的 Apply/Cancel（左键落点 / 右键撤销）都是 private，
    /// 且游戏没有任何公开的「加一个控制点」入口（FACT：AreaToolSystem 公开面见反编译 ATS:2585-3223）。
    /// 补丁只用来收两个事件，其余改动全走 ECS 数据层 —— 见 Patches/AreaToolPatches.cs 的长注释。
    /// </summary>
    public class ZoneSnapperMod : IMod
    {
        /// <summary>
        /// 版本字面量。⚠ 必须与 Properties/PublishConfiguration.xml 的 &lt;ModVersion&gt; 一致，
        /// 由 scripts/verify.mjs 的版本一致性检查钉死（版本号散多处靠人记必失 —— Playbook 步骤 4）。
        /// </summary>
        public const string kVersion = "0.3.0";

        public static ILog log = LogManager.GetLogger("ZoneSnapper").SetShowsErrorsInUI(false);

        private ZoneSnapperSetting m_Setting;
        private Harmony m_Harmony;

        public void OnLoad(UpdateSystem updateSystem)
        {
            log.Info($"Zone Snapper v{kVersion} loading (snap + trace along networks; game 1.6.2f1; local build, not published)...");

            try
            {
                if (GameManager.instance != null && GameManager.instance.modManager != null
                    && GameManager.instance.modManager.TryGetExecutableAsset(this, out var asset))
                {
                    log.Info("模组目录：" + asset.path);
                }
            }
            catch (Exception) { }

            // —— 1) 选项页对象 + 2) 多语言 + 3) 上屏。顺序照 AccessAnarchy（:34 AddSource → :42 上屏）：
            //      词条源必须先注册，再让游戏建页；反过来的顺序虽然大概率也能查到，但没必要偏离一个已验证的形状。
            m_Setting = new ZoneSnapperSetting(this);
            ZoneSnapperSetting.Instance = m_Setting;

            // 多语言：每份字典各自构建（Playbook §3.3），**必须在注册选项页之前**。
            //      v0.1.0/0.1.1 的「选项页全是 key」是因为注册的词条名是裸属性名，与游戏查的
            //      Options.OPTION[<id>.<类名>.<属性名>] 一条都对不上，
            //      查不到时游戏直接把 key 画上（FACT：Game.UI.Localization/UILocalizationManager.cs:19-29）。
            try
            {
                string[] locales = ZoneSnapperSetting.SupportedLocales();
                for (int i = 0; i < locales.Length; i++)
                {
                    GameManager.instance.localizationManager.AddSource(
                        locales[i], new ZoneSnapperLocaleSource(m_Setting, locales[i]));
                }
                log.Info($"语言源已注册 {locales.Length} 份，当前语言={GameManager.instance.localizationManager.activeLocaleId}");
                // key 前缀打出来供离线核对：面板上任何一行长成 Options.… 都可以从这一段起对账
                log.Info("词条 key 前缀 id=" + m_Setting.id + " name=" + m_Setting.name);
                if (ZoneSnapperSetting.TemplateDiff != null)
                {
                    log.Warn("游戏的 key 模板与我们抄的不一致 ⇒ 选项页会退回显示 key：" + ZoneSnapperSetting.TemplateDiff);
                }
                if (ZoneSnapperSetting.MissingSlugs.Count > 0)
                {
                    log.Warn("缺词条 " + ZoneSnapperSetting.MissingSlugs.Count + " 条（面板上会显示英文或 key）："
                             + string.Join(", ", ZoneSnapperSetting.MissingSlugs.ToArray()));
                }
            }
            catch (Exception e)
            {
                log.Warn("注册语言源失败（界面会退回 key 显示）：" + e.GetType().Name);
            }

            // RegisterInOptionsUI 在世界未就绪时会静默返回 false（void 重载吞掉返回值）⇒ 先判世界。
            if (Unity.Entities.World.DefaultGameObjectInjectionWorld != null)
            {
                m_Setting.RegisterInOptionsUI();
                log.Info("选项页已注册");
            }
            else
            {
                log.Warn("世界未就绪，选项页未注册（本局需要重开一次模组选项页；实机 T4 哨兵项）");
            }

            // —— 3) 读玩家存过的设置（必须在注册键位之前）
            try
            {
                Colossal.IO.AssetDatabase.AssetDatabase.global.LoadSettings(
                    nameof(ZoneSnapper), m_Setting, new ZoneSnapperSetting(this));
            }
            catch (Exception e)
            {
                log.Warn("读取存档设置失败，按默认值运行：" + e.GetType().Name);
            }
            m_Setting.Sync();

            // —— 4) 快捷键
            RegisterHotkeys();

            // —— 5) Harmony 补丁：**必须先于注册系统**。
            //      v0.1.0 的实机事故就是顺序反了：第 6 步注册 ZoneSnapperApplySystem 时
            //      TypeManager 抛 ArgumentException（DynamicBuffer<Game.Areas.Node> 对模组程序集不注册），
            //      OnLoad 当场中断 ⇒ 补丁一个都没打上，而第 5 步之前注册的系统已经在跑：
            //      它带着一个永远空着的手动栈每帧改写控制点表，把玩家刚点下的第一个节点抹掉，
            //      表现是「区域工具点了没反应」。看日志的证据：只有 ZoneSnapperSystem 的统计行，
            //      没有 [Hook] 那一行，且 manual=0 恒定。
            m_Harmony = new Harmony("com.qoder.zonesnapper");
            string hookErr = null;
            int patched = 0;
            try
            {
                patched = AreaToolPatches.Install(m_Harmony, out hookErr);
            }
            catch (Exception e)
            {
                hookErr = e.GetType().Name + ": " + e.Message;
            }
            SnapperState.HooksInstalled = patched;
            // 实机哨兵行：这一行数字不是 2 就说明游戏改了方法签名，模组会「装了但没效果」。
            log.Info($"[Hook] 补丁已安装 {patched}/2 个" + (hookErr != null ? "；问题：" + hookErr : ""));

            // —— 6) 系统：排在 AreaToolSystem 之后、同一 ToolUpdate 相位
            //      （FACT：AreaToolSystem 注册在 SystemUpdatePhase.ToolUpdate，SystemOrder.cs:700；
            //        UpdateAfter<T,Other>(phase) 见 Game/UpdateSystem.cs:178）
            bool mainOk = RegisterSystems(updateSystem);

            // 分两级降级，口径是「坏到什么程度就退到哪一步」：
            //  · 一个补丁都没打上、或主系统没挂上 ⇒ **整机不介入**（此时我们既收不到落点事件、
            //    也说不清表里有几个玩家的点，动它只会重演 v0.1.0 那个「点不下第一个节点」）；
            //  · 只漏了 Cancel 那一个补丁 ⇒ 继续跑，但右键撤销会与我们不同步：
            //    保护动作在 ProjectKit.MayWrite（要减点时必须先证明「表里这些格全是我们自己写的」）
            //    + Desyncs 计数，宁可这一帧不写。
            //  · ZoneSnapperApplySystem 没起来只影响「闭合那条边是直的」，不牵动主链路。
            if (patched == 0 || !mainOk)
            {
                SnapperState.Broken = true;
                log.Error($"Zone Snapper 降级为「不介入」（补丁 {patched}/2、主系统 {(mainOk ? "就绪" : "未就绪")}）："
                          + "本模组不会改写区域工具，请在日志里找上面那行原因");
            }
            else if (patched < 2)
            {
                log.Warn("只有 1/2 个补丁打上：右键撤销不会同步到本模组的手动栈（会看到 desync 计数增长）");
            }
        }

        /// <summary>注册三个系统，返回**主系统**是否就绪（另两个各自失败只降级自己那条功能，不算整机失败）。</summary>
        private bool RegisterSystems(UpdateSystem updateSystem)
        {
            bool mainOk = false;
            try
            {
                updateSystem.UpdateAfter<ZoneSnapperSystem, AreaToolSystem>(SystemUpdatePhase.ToolUpdate);
                mainOk = true;
                log.Info("ZoneSnapperSystem 已注册：ToolUpdate 相位、紧随 AreaToolSystem（每帧在其之后改写控制点表）");
            }
            catch (Exception e)
            {
                log.Error("ZoneSnapperSystem 注册失败：" + e.GetType().Name + " " + e.Message);
            }

            // 第二个系统跑在 ApplyTool 相位、紧随 ApplyAreasSystem（FACT：SystemOrder.cs:718 把 ApplyAreasSystem
            // 注册在 SystemUpdatePhase.ApplyTool）。必须同帧：区域的 Created 标记在帧末 Cleanup 相位就被回收
            // （FACT：Game.Common/CleanUpSystem.cs:24-53），隔帧再找只能靠昂贵的全量比对。
            // 它的职责是把「闭合边」的描边补写进真正落档的区域多边形。
            // ⚠ 这条在 v0.1.0 实机里抛了 Unknown Type: DynamicBuffer<Game.Areas.Node> —— 见 OnLoad 第 5 步的注释。
            try
            {
                updateSystem.UpdateAfter<ZoneSnapperApplySystem, ApplyAreasSystem>(SystemUpdatePhase.ApplyTool);
                log.Info("ZoneSnapperApplySystem 已注册：ApplyTool 相位、紧随 ApplyAreasSystem（补写闭合边）");
            }
            catch (Exception e)
            {
                // 少了它只影响「闭合那一条边是直的」，不影响贴合与描边主链路 ⇒ 单独降级、单独说清。
                log.Error("ZoneSnapperApplySystem 注册失败（闭合边补写不可用，其余功能不受影响）："
                          + e.GetType().Name + " " + e.Message);
            }

            // 第三个系统：已提交区域的自动跟随 + 拖拽改形后的重描（需求 5 / 反馈 8）。
            //
            // ⚠ 相位在第五轮改过一次，改的原因就是「四轮日志 follow=0」：
            //   原来挂在 ApplyTool，而那一相位**只在工具真的提交那一帧才存在**
            //   （FACT：Game.Tools/ToolOutputSystem.cs:20-30 switch(applyMode){ Apply → Update(ApplyTool); Clear → Update(ClearTool) }），
            //   可「改了路」与「按了重算键」这两件事本身都不产生工具提交 ⇒ OnUpdate 根本不跑。
            //   现在挂 ToolUpdate（每帧必跑）并排在主系统之后 —— 那时 DragArmed 与拖拽目标已经留好。
            //   路网脏标记的读取也只有在 ToolUpdate 才成立：UpdateCollectSystem 在 Modification5 置位
            //   （FACT：Game.Net/UpdateCollectSystem.cs:389-413 + SystemOrder.cs:192），
            //   而 Modification5 排在 ToolUpdate **之前**（FACT：Game/SystemUpdatePhase.cs 的枚举顺序）。
            try
            {
                updateSystem.UpdateAfter<ZoneSnapperFollowSystem, ZoneSnapperSystem>(SystemUpdatePhase.ToolUpdate);
                log.Info("ZoneSnapperFollowSystem 已注册：ToolUpdate 相位、紧随主系统（自动跟随、拖拽后重描、跟随退回）");
            }
            catch (Exception e)
            {
                log.Error("ZoneSnapperFollowSystem 注册失败（自动跟随与拖拽重描不可用，绘制中的贴合不受影响）："
                          + e.GetType().Name + " " + e.Message);
            }

            // 第四个系统：实时预览的渲染端（第四轮反馈 2）。相位是 Rendering，且必须排在 OverlayRenderSystem
            // 之前 —— 它就是往那张 overlay 缓冲里写线的，排在后面只能等下一帧（FACT：SystemOrder.cs:688 把
            // OverlayRenderSystem 注册在 SystemUpdatePhase.Rendering，UpdateSystem.cs:173 提供 UpdateBefore<,>）。
            // 它只画不写：区域数据、控制点表都不经过这个系统，注册失败的代价就是「没有预览」。
            try
            {
                updateSystem.UpdateBefore<ZoneSnapperPreviewSystem, OverlayRenderSystem>(SystemUpdatePhase.Rendering);
                log.Info("ZoneSnapperPreviewSystem 已注册：Rendering 相位、先于 OverlayRenderSystem（淡色实时预览）");
            }
            catch (Exception e)
            {
                log.Error("ZoneSnapperPreviewSystem 注册失败（没有淡色预览，贴合与描边不受影响）："
                          + e.GetType().Name + " " + e.Message);
            }
            return mainOk;
        }

        private void RegisterHotkeys()
        {
            try
            {
                if (!m_Setting.keyBindingRegistered) m_Setting.RegisterKeyBindings();
                HookAction(ZoneSnapperSetting.kActionToggleEnabled);
                HookAction(ZoneSnapperSetting.kActionCycleMode);
                HookAction(ZoneSnapperSetting.kActionRetraceNow);
                HookAction(ZoneSnapperSetting.kActionUndoFollow);
            }
            catch (Exception e)
            {
                log.Warn("注册快捷键失败（功能仍可用，只是没键）：" + e.GetType().Name + " " + e.Message);
            }
        }

        private void HookAction(string actionName)
        {
            ProxyAction action = m_Setting.GetAction(actionName);
            if (action == null)
            {
                log.Warn("取不到动作：" + actionName);
                return;
            }

            // 只有「真的绑到一颗键」才启用。没绑键就 shouldBeEnabled=true 会让输入系统按设备前缀匹配
            // 整块键盘 ⇒ 玩家碰任何键都触发（Playbook §3.2 的幻影触发事故）。
            bool bound = HasBindableBinding(action);
            action.shouldBeEnabled = bound;
            if (!bound)
            {
                log.Info($"动作 {actionName} 当前没有可用绑定，暂不启用（去选项页「快捷键」里设一颗键）");
                return;
            }
            action.onInteraction += (ProxyAction a, InputActionPhase phase) =>
            {
                if (phase != InputActionPhase.Started) return;   // 只要按下沿
                try
                {
                    ZoneSnapperSetting s = Instance;
                    if (s != null) s.OnHotkey(a.name);
                }
                catch (Exception e)
                {
                    log.Warn("快捷键处理异常：" + e.GetType().Name);
                }
            };
            log.Info($"快捷键已挂：{actionName}");
        }

        private static bool HasBindableBinding(ProxyAction action)
        {
            try
            {
                foreach (ProxyBinding b in action.bindings)
                {
                    if (InputKit.IsBindablePath(b.path)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        private static ZoneSnapperSetting Instance
        {
            get { return ZoneSnapperSetting.Instance; }
        }

        public void OnDispose()
        {
            try
            {
                // UnpatchAll(harmonyId) 在 HarmonyX 里是 obsolete-as-error（CS0619）⇒ 用 UnpatchSelf()。
                AreaToolPatches.Uninstall(m_Harmony);
            }
            catch (Exception e)
            {
                log.Warn("卸载补丁失败：" + e.GetType().Name);
            }

            try
            {
                if (m_Setting != null)
                {
                    m_Setting.UnregisterInOptionsUI();
                    ZoneSnapperSetting.Instance = null;
                }
            }
            catch (Exception e)
            {
                log.Warn("注销选项页失败：" + e.GetType().Name);
            }

            SnapperLog.Info($"Zone Snapper v{kVersion} 已卸载（累计：提交 {SnapperState.Commits} 撤销 {SnapperState.Pops} " +
                            $"贴合命中 {SnapperState.SnapHits} 落空 {SnapperState.SnapMisses} 描边 {SnapperState.TraceHits} 回退 {SnapperState.TraceFallbacks} " +
                            $"接管 {SnapperState.Adopted} 拒写 {SnapperState.Desyncs} 补丁 {SnapperState.HooksInstalled}/2）");
        }
    }
}

using System;
using Colossal.Mathematics;
using Game;
using Game.Rendering;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Scripting;
using ZoneSnapper.Engine;
using ZoneSnapper.GameSide;

namespace ZoneSnapper.Systems
{
    /// <summary>
    /// 实时预览的渲染端（第四轮反馈 2）：玩家还在移动下一个要放的节点、或拖着已存在的节点还没松手时，
    /// 用**更淡的颜色**把「落下去会贴合成的结果」先画出来。
    ///
    /// 【走的是哪条通道】<see cref="OverlayRenderSystem"/> 的公开 <c>Buffer</c>：
    ///  · <c>GetBuffer(out JobHandle)</c>（FACT：OverlayRenderSystem.cs:602）拿到写入器，
    ///    <c>DrawLine(Color, Line3.Segment, float)</c>（:201）画线、
    ///    <c>DrawCircle(Color, float3, float)</c>（:133）画节点圈；
    ///  · 颜色是 <c>UnityEngine.Color</c>，alpha 有效（游戏自己也用 alpha 0.5 画悬停/警告色，
    ///    FACT：AreaBorderRenderSystem.cs:691-694），内部会转 linear，所以我们按普通 gamma 颜色传。
    /// 为什么不用游戏的 ControlPoint 渲染：控制点那一格没有颜色通道，淡色这件事在游戏侧做不到。
    ///
    /// 【时序】预览的**内容**由 ToolUpdate 里的 ZoneSnapperSystem 发布到 <see cref="PreviewFeed"/>；
    /// 本系统排在 OverlayRenderSystem 之前提交（注册见 ZoneSnapperMod.cs）。
    /// 注意 <c>SystemUpdatePhase</c> 枚举里 Rendering 排在 ToolUpdate **之前**，
    /// 所以最坏情况是晚一帧上屏（≈16 毫秒）—— 预览晚一帧不伤人，卡手才伤人。
    ///
    /// 【一帧一清】OverlayRenderSystem 每帧在 OnUpdate 里把实例计数归零（:741-747）⇒ 想要就一直提交，
    /// 停止提交就是自动消失，本系统不需要任何「擦除」代码。
    ///
    /// 【但是「没新东西」不等于「把旧的撤掉」——第五轮反馈 1】玩家只放了一个节点就按 ESC 退出工具，
    /// 淡色的线和节点仍留在地上。渲染端因此自己再加两道闸，不再完全信任生产端：
    ///  ① **区域工具必须还是当前工具**（FACT：Game.Tools/ToolSystem.cs:84-100 <c>activeTool</c> 是公开属性，
    ///    :312-324 切换时把上一个工具 <c>Enabled=false</c> 再跑一帧、当前工具 <c>Enabled=true</c>；
    ///    <c>AreaToolSystem : ToolBaseSystem</c>，FACT：AreaToolSystem.cs:29）⇒ 工具一走，预览立刻不再画。
    ///  ② **新鲜度**：预览是 ToolUpdate 那一帧发布的，超过 <see cref="STALE_FRAMES"/> 帧没有重新发布
    ///    （生产端整个停摆、异常兜底、相位没被驱动都会这样）⇒ 一律作废。
    /// 用 <c>Time.frameCount</c> 而不是我们自己的帧计数：生产端停摆时我们的计数器也停了，
    /// 拿它量「多久没更新」永远量出 0。
    ///
    /// 【绝不让预览伤到绘制】整个 OnUpdate 包在 try/catch 里，任何一次异常只把**预览**关掉
    /// （m_broken），控制点表、区域数据一个字节都不经过本系统。
    /// </summary>
    [Preserve]
    public partial class ZoneSnapperPreviewSystem : GameSystemBase
    {
        /// <summary>抬离地面的高度（米）：避开与地表/路面的 z-fighting，也让贴地折线在曲面上不断裂。</summary>
        private const float LIFT = 0.35f;

        /// <summary>预览几何的保鲜帧数。正常节奏是「ToolUpdate 发布 → 下一帧 Rendering 画」，留 4 帧余量。</summary>
        private const int STALE_FRAMES = 4;

        private OverlayRenderSystem m_overlay;
        private ToolSystem m_toolSystem;
        private AreaToolSystem m_areaTool;
        private bool m_broken;

        /// <summary>闸为什么关掉它，只说一次（实机「预览不见了」的第一手证据）。</summary>
        private bool m_loggedGateOff;

        /// <summary>只在「这一局第一次真的往缓冲里写了东西」时记一行（实机判读那条 Rendering 相位注册是否落地的证据）。</summary>
        private bool m_loggedFirst;

        /// <summary>同上，但是跟随高亮那一路：玩家说「没看到改动提示」时，这一行与 Notified 一比就知道断在哪。</summary>
        private bool m_loggedFlash;

        protected override void OnCreate()
        {
            base.OnCreate();
            try
            {
                m_overlay = World.GetOrCreateSystemManaged<OverlayRenderSystem>();
                m_toolSystem = World.GetOrCreateSystemManaged<ToolSystem>();
                m_areaTool = World.GetOrCreateSystemManaged<AreaToolSystem>();
                SnapperLog.Info("[ZoneSnapper] 预览渲染就绪：OverlayRenderSystem/ToolSystem/AreaToolSystem 均已取得");
            }
            catch (Exception e)
            {
                m_overlay = null;
                m_broken = true;
                SnapperLog.Error("[ZoneSnapper] 取 OverlayRenderSystem 失败，实时预览不显示（其余功能不受影响）：" +
                                 e.GetType().Name + " " + e.Message);
            }
        }

        protected override void OnUpdate()
        {
            if (m_broken || m_overlay == null) return;
            if (SnapperState.Broken) return;

            ZoneSnapperSetting inst = ZoneSnapperSetting.Instance;
            if (inst != null) inst.PollSync();      // 第六轮：滑杆值每帧对指纹，不依赖 setter 是否被 UI 调到
            ModConfig cfg = SnapperState.Config;
            if (cfg == null || !cfg.Enabled) return;

            // 同一条 overlay 通道服务两件事，各走各自的开关（反馈 8 的高亮不能跟着预览一起被关掉：
            // 预览是锦上添花，高亮是「模组改了玩家存档之后必须让他看见」）。
            bool drew = false;
            try
            {
                if (cfg.ShowLivePreview) drew |= DrawPreview(cfg);
                if (cfg.HighlightFollowedAreas) drew |= DrawFlashes(cfg);
                if (drew) PreviewFeed.Draws++;
            }
            catch (Exception e)
            {
                // 预览是唯一受害者：关掉它，绘制与贴合逻辑一行都没经过这里。
                m_broken = true;
                SnapperLog.Error("[ZoneSnapper] 覆盖层绘制失败，本局停止预览与跟随高亮（贴合与描边不受影响）：" +
                                 e.GetType().Name + " " + e.Message);
            }
        }

        /// <summary>淡色实时预览（第四轮反馈 2）。返回「这一帧到底画了没有」。</summary>
        private bool DrawPreview(ModConfig cfg)
        {
            PreviewKit.Result res = PreviewFeed.Current;
            if (res == null || !res.HasContent) return false;

            // 渲染端自己的两道闸（见类注释「第五轮反馈 1」）：工具已经不在了、或这份预览已经不新鲜了，
            // 就当场作废 —— 玩家按 ESC 退出后地上还留着淡色线，就是这一格没人管造成的。
            if (!ToolIsLive() || !PreviewFeed.IsFresh(STALE_FRAMES))
            {
                PreviewFeed.Drop();
                LogGateOnce();
                return false;
            }

            JobHandle deps;
            OverlayRenderSystem.Buffer buffer = m_overlay.GetBuffer(out deps);
            CompleteWriters(deps);

            float a = (float)GeoKit.Clamp(cfg.PreviewAlpha, 0.05, 1.0);
            Color line = new Color(0.55f, 0.86f, 1f, a);      // 淡青：将要沿路走出来的那条边
            Color dot = new Color(1f, 0.84f, 0.32f, a);       // 淡黄：将要真的落进区域里的节点
            float w = res.LineWidth > 0.02f ? res.LineWidth : 0.3f;
            float d = res.DotDiameter > 0.05f ? res.DotDiameter : 1.2f;

            for (int s = 0; s < res.Segments.Count; s++)
            {
                System.Collections.Generic.List<P3> pts = res.Segments[s].Points;
                for (int i = 1; i < pts.Count; i++)
                {
                    buffer.DrawLine(line, new Line3.Segment(At(pts[i - 1]), At(pts[i])), w);
                }
            }
            for (int i = 0; i < res.Dots.Count; i++)
            {
                buffer.DrawCircle(dot, At(res.Dots[i]), d);
            }

            if (!m_loggedFirst)
            {
                m_loggedFirst = true;
                SnapperLog.Info(string.Format(
                    "[ZoneSnapper] 预览已上屏：淡色折线 {0} 段、节点 {1} 个（不透明度 {2:F2}）",
                    res.Segments.Count, res.Dots.Count, cfg.PreviewAlpha));
            }
            return true;
        }

        /// <summary>
        /// 跟随改动高亮（第五轮反馈 8）：被自动跟随改过的那一圈边界描实色一圈，
        /// 被挪动的节点闪烁，若干秒后自己消失（有效期由 <see cref="FollowFlashFeed"/> 管）。
        /// </summary>
        private bool DrawFlashes(ModConfig cfg)
        {
            if (!FollowFlashFeed.HasContent) return false;

            JobHandle deps;
            OverlayRenderSystem.Buffer buffer = m_overlay.GetBuffer(out deps);
            CompleteWriters(deps);

            // 闪烁：用非缩放时间做正弦，最后一秒线性淡出 —— 「闪一会儿然后过一会儿消失」就是这个形状。
            float now = UnityEngine.Time.unscaledTime;
            System.Collections.Generic.List<FollowFlashFeed.Flash> list = FollowFlashFeed.Active();
            for (int f = 0; f < list.Count; f++)
            {
                FollowFlashFeed.Flash fsh = list[f];
                float life = fsh.Until - now;
                if (life <= 0f) continue;
                float fade = UnityEngine.Mathf.Clamp01(life);                       // 最后 1 秒淡出
                float blink = 0.55f + 0.45f * UnityEngine.Mathf.Sin(now * 9f);      // 约 1.4 Hz
                float a = UnityEngine.Mathf.Clamp01(fade * blink);

                Color ringCol = new Color(0.35f, 1f, 0.55f, a);                     // 淡绿：这一块被改过
                Color movedCol = new Color(1f, 0.45f, 0.35f, UnityEngine.Mathf.Clamp01(fade));  // 橙红：被挪动的节点
                float w = 0.45f;
                for (int i = 0; i < fsh.Ring.Count; i++)
                {
                    P3 p = fsh.Ring[i];
                    P3 q = fsh.Ring[(i + 1) % fsh.Ring.Count];
                    buffer.DrawLine(ringCol, new Line3.Segment(At(p), At(q)), w);
                }
                for (int i = 0; i < fsh.Moved.Count; i++)
                {
                    buffer.DrawCircle(movedCol, At(fsh.Moved[i]), 1.8f);
                }
            }

            if (!m_loggedFlash)
            {
                m_loggedFlash = true;
                SnapperLog.Info("[ZoneSnapper] 跟随高亮已上屏：" + list.Count + " 块区域的边界正在闪烁（约 "
                                + FollowFlashFeed.LIFE_SECONDS + " 秒后自动消失）");
            }
            return true;
        }

        /// <summary>
        /// 别的系统（GuideLines / AreaBorder / EffectRange）把写任务登记在 OverlayRenderSystem 那边，
        /// GetBuffer 交回来的 handle 就是它们未完的写。主线程要往同一个 NativeList 里 Add，
        /// 必须先等它们做完，否则 Unity.Collections 当场抛「The job instance is currently executing」。
        /// </summary>
        private static void CompleteWriters(JobHandle deps)
        {
            deps.Complete();
        }

        /// <summary>
        /// 区域工具还是当前工具吗？拿不到工具系统时**放行**（宁可多画一帧，也不要因为一次读取失败
        /// 就把玩家要的功能整个关掉）；拿到就要求 activeTool 正是 AreaToolSystem。
        /// </summary>
        private bool ToolIsLive()
        {
            try
            {
                if (m_toolSystem == null || m_areaTool == null) return true;
                if (m_toolSystem.activeTool != m_areaTool) return false;
                AreaToolSystem.State st = m_areaTool.state;
                return st == AreaToolSystem.State.Create || st == AreaToolSystem.State.Modify;
            }
            catch (Exception)
            {
                return true;
            }
        }

        private void LogGateOnce()
        {
            if (m_loggedGateOff) return;
            m_loggedGateOff = true;
            SnapperLog.Info("[ZoneSnapper] 预览已按「工具不在/几何过期」作废（这条每次上屏后只记一次）");
        }

        /// <summary>模组卸载：信箱复位，下一局从干净状态开始。</summary>
        protected override void OnDestroy()
        {
            base.OnDestroy();
            PreviewFeed.Drop();
        }

        /// <summary>把引擎坐标换成 overlay 的世界坐标（y 是高度，所以抬 LIFT 米避开 z-fighting）。</summary>
        private static float3 At(P3 p)
        {
            return new float3((float)p.X, (float)p.H + LIFT, (float)p.Y);
        }
    }
}

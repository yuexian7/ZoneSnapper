using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 贴合模式（第五轮反馈 5：原来的「贴合优先级」那一板块整个删掉，换成这个下拉框）。
    ///
    /// 【为什么做成模式而不是三个开关】玩家要的是「这一次画的时候该怎么选路」，
    /// 而上一版把它拆成「优先交叉口 / 优先已有边界 / 允许贴路缘」三个各自独立的开关 ——
    /// 三个开关互相之间谁赢没有定义，玩家也说不清它们各管什么。模式把这层取舍收回到一处，
    /// 每个模式就是一组**具体数字**（见 <see cref="ModeKnobs"/>），可断言、可复述。
    ///
    /// 【默认 Smart 的理由】其余四个都是「拿某一方面的质量换另一方面」：
    ///  · 已有区域边界永远允许吸（不再有开关）：反馈 4 明确要「两个手动节点都贴在另一个专门产业区的边缘上时
    ///    要自动贴合那个产业区的节点」，这是功能不是偏好；
    ///  · 路缘档永远允许：它由类别决定（市辖区=中心线，产业区/表面区域=外沿），不再做成开关。
    /// </summary>
    public enum TraceMode
    {
        /// <summary>智能（默认）：走最外围路段、交点必放节点、中断处续接、绕行与角度受上限约束。</summary>
        Smart = 0,
        /// <summary>路径最短：只求两点之间沿路最短，不追求外围；转弯与换类几乎不加代价。</summary>
        Shortest,
        /// <summary>节点最少：宁可用直线连过去，也少放节点（只有明显拐点才放点）。</summary>
        FewestNodes,
        /// <summary>交点最少：尽量沿同一条路走完，少在路口拐弯，允许为此多走一段。</summary>
        FewestCorners,
        /// <summary>不切换网络：只在两端各自所属的网络上走，绝不从一条路跳到另一条路。</summary>
        NoNetworkSwitch,
    }

    /// <summary>一个模式对应的一组代价/阈值。字段全是**数值**，便于离线断言与实机调参。</summary>
    public struct ModeKnobs
    {
        /// <summary>绕行上限（×两点直线距离）。</summary>
        public double MaxDetourFactor;
        /// <summary>转弯代价系数（×(1-cosθ)）。</summary>
        public double TurnPenalty;
        /// <summary>换网络类型的代价（米，等效长度）。</summary>
        public double KindChangePenalty;
        /// <summary>许不许为了「包住街区外侧」而专门各搜一遍左/右侧（需求 2 的外围选路）。</summary>
        public bool PreferOutermost;
        /// <summary>许不许跨到另一条网络（切换处）。</summary>
        public bool AllowNetworkSwitch;
        /// <summary>拐点判定（度）：转角小于这个数不算「交点」，可以不放节点。</summary>
        public double CornerDegrees;
    }

    /// <summary>
    /// 把「界面上的开关/滑杆」翻译成「算法要用的具体数值」。单独成层的原因：
    /// 需求 7（贴合范围要按区域类别分级）是纯策略，必须能离线断言，而不能等到实机靠手感调。
    /// </summary>
    public sealed class ModConfig
    {
        // —— 总开关（反馈 2：默认打开）——
        public bool Enabled = true;

        // —— 生效的区域类型：只对应游戏「区域」工具里的三个工具（反馈 2）——
        public bool SnapDistrict = true;
        public bool SnapLot = true;
        public bool SnapSurface = true;
        // 「特殊吸附目标」板块：城市边界线（游戏里的 MapTile）。原「太空区域」按反馈 6 整条删除。
        public bool SnapMapTile = false;

        // —— 节点吸附距离（反馈 3：每类一条，50%~300%，默认 100% = 游戏原版距离）——
        public double DistrictSnapDistance = 1.0;
        public double LotSnapDistance = 1.0;
        public double SurfaceSnapDistance = 1.0;
        public double MapTileSnapDistance = 1.0;
        /// <summary>半径下限/上限（米）。上限存在的意义：大区域在旷野里也不会被一公里外的路拽走。</summary>
        public double RadiusFloor = 8;
        public double RadiusCeiling = 260;

        // —— 贴合模式（反馈 5：取代原「贴合优先级」板块的三个开关）——
        public TraceMode Mode = TraceMode.Smart;

        // —— 描边：不再是开关（反馈 7「边线沿网络描边都默认允许，不要显示了」）——
        /// <summary>环形道路：按反馈 7 默认**沿环岛自己的中心线**走，不再刻意绕到街区最外围。</summary>
        public const bool TraceAlongNetworks = true;
        public const bool TraceAcrossNetworkKinds = true;
        /// <summary>
        /// 两点落在同一条环形路上时，允许改走环的另一侧（避免把区域压成一条缝）。
        /// 反馈 7 把它从设置项里删掉了，但行为保留：绕的仍然是**这个环自己**，不是环岛外围那一圈路。
        /// </summary>
        public const bool TraceAroundRings = true;
        /// <summary>单条边的节点数上限：反馈 7 要求「无限」。这里只保留一道存档侧的硬保险。</summary>
        public const int HARD_NODE_CAP = 4000;
        /// <summary>A* 一条边最多展开多少个图节点（性能闸，不是玩家能调的东西）。</summary>
        public const int MAX_GRAPH_NODES_PER_EDGE = 400;


        // —— 节点密度与弯曲（反馈 7：原来那三个「自由点走弧线」的设置项删掉，换成曲线精细度一档）——
        /// <summary>
        /// 曲线精细度 0..1（滑杆 0..100，**默认 0**）：0 = 只在交点放节点（弯道用尽量少的折点近似），
        /// 越大越密（弯道处补点）。它取代旧的「简化程度」，方向相反、语义更直白。
        /// </summary>
        public double CurveDetail = 0.0;
        /// <summary>相邻自动节点最小间距（米），防止重合点让游戏三角化失败。</summary>
        public double MinAutoNodeSpacing = 2.0;

        // —— 节点续接（反馈 7 新增，取代「自由点之间走弧线」）——
        /// <summary>
        /// 两端之间没有**连续**的自动贴合路径时：能贴的那一段照样贴，剩下的用直线连上。
        /// 至少要有两端之一贴在道路/轨道/建筑边缘上，且仍受绕行上限与角度闸约束。
        /// </summary>
        public bool NodeContinuation = true;

        // —— 自动跟随（反馈 8：绘制过程中那一条不再做成设置项——绘制时不可能去改路）——
        /// <summary>
        /// 已提交区域：路网变了以后自动改写边界。**实验性**且默认关（它改的是存档）。
        /// </summary>
        public bool FollowCommittedAreas = false;

        // —— 可视化 ——
        /// <summary>跟随改动之后把那块区域的边界描一圈、被挪动的节点闪一下（反馈 8 对「显示贴合提示」的重定义）。</summary>
        public bool HighlightFollowedAreas = true;
        /// <summary>
        /// 实时预览（第四轮反馈 2）：光标还在移动、或已存在的节点正被拖着未松手时，
        /// 先用淡色画出「点下去/松手后会变成什么样」。渲染在 Systems/ZoneSnapperPreviewSystem.cs。
        /// 第五轮把它从「贴合」页挪到「其它设置」页（反馈 7）。
        /// </summary>
        public bool ShowLivePreview = true;
        /// <summary>预览色的不透明度 0..1（滑杆 10..90%）。刻意低于实线，避免把预览看成已提交结果。</summary>
        public double PreviewAlpha = 0.35;

        public static ModConfig CreateDefault()
        {
            return new ModConfig();
        }
    }

    /// <summary>某个区域类别算出来的实际数值。所有算法只看这个，不看界面状态。</summary>
    public struct ResolvedTuning
    {
        public AreaTier Tier;
        /// <summary>节点吸附距离（米）= 游戏给这一类配的 m_SnapDistance × 玩家那条滑杆。</summary>
        public double SnapRadius;
        /// <summary>该档当前的贴合模式（反馈 5）。</summary>
        public TraceMode Mode;
        /// <summary>模式翻译成的一组代价/阈值（<see cref="PolicyKit.ModeKnobs(TraceMode)"/>）。</summary>
        public ModeKnobs Knobs;
        /// <summary>曲线精细度 0..1（滑杆 0..100）。</summary>
        public double CurveDetail;
        /// <summary>弯道上允许的弦高（米）：越大折点越少。由曲线精细度与道路宽度共同决定。</summary>
        public double SimplifyTolerance;
        /// <summary>
        /// 弦高硬界（米）：这一档最松也不许超过它 —— 见 <see cref="ChordTolerance"/>。
        /// 这里是「不知道具体路宽」时的值；描边时会按用到的每条边的真实路宽再收紧一次。
        /// </summary>
        public double ChordTol;
        /// <summary>拐点判定（度）：转角小于它不算「交点」，可以不放节点（反馈 4）。</summary>
        public double CornerDegrees;
        public int MaxNodesPerEdge;
        public double MinSpacing;
        public double MaxDetourFactor;
        public bool SnapEnabled;
        public bool TraceEnabled;
        /// <summary>节点续接（反馈 7）：没有连续路径时能贴的照样贴、剩下的用直线连。</summary>
        public bool ContinuationEnabled;
    }

    /// <summary>
    /// 策略解析。设计口径：
    /// 基准半径不用「我们猜一个数」，而用游戏 prefab 自己的 AreaGeometryData.m_SnapDistance
    /// （FACT：Game.Prefabs.AreaGeometryData 有 m_SnapDistance；AreaUtils.GetMinNodeDistance = m_SnapDistance*0.5f）。
    /// 游戏已经按区域类别给好了尺度差异，玩家那一档滑杆就是乘在它上面的百分比（反馈 3：50%~300%，默认 100%），
    /// 不再有「统一倍率 + 行政区额外倍率」两层相乘 —— 那两套数值叠在一起，玩家看不出自己调的到底是什么。
    /// </summary>
    public static class PolicyKit
    {
        /// <summary>该类别的「节点吸附距离」倍率（反馈 3：每类一条滑杆，互不影响）。</summary>
        public static double SnapDistanceScale(ModConfig cfg, AreaTier tier)
        {
            if (cfg == null) return 1.0;
            switch (tier)
            {
                case AreaTier.District: return cfg.DistrictSnapDistance;
                case AreaTier.Lot: return cfg.LotSnapDistance;
                case AreaTier.Surface: return cfg.SurfaceSnapDistance;
                case AreaTier.MapTile: return cfg.MapTileSnapDistance;
                default: return 1.0;
            }
        }

        /// <summary>
        /// 玩家能不能在选项页上看到/调到这一档。
        /// 太空区按反馈 6 整条删除 ⇒ <see cref="AreaTier.Space"/> 不再参与（返回 false，画太空区域时模组完全不介入）。
        /// </summary>
        public static bool TierEnabled(ModConfig cfg, AreaTier tier)
        {
            if (!cfg.Enabled) return false;
            switch (tier)
            {
                case AreaTier.District: return cfg.SnapDistrict;
                case AreaTier.Lot: return cfg.SnapLot;
                case AreaTier.Surface: return cfg.SnapSurface;
                case AreaTier.MapTile: return cfg.SnapMapTile;
                case AreaTier.Space: return false;
                default: return false;     // 认不出类别时不介入：拿错档去改玩家的几何比不动手更糟
            }
        }

        /// <summary>五个模式的具体数字。改动这里必须同步改回归壳 O5 段（模式之间不许互相串味）。</summary>
        public static ModeKnobs ModeKnobs(TraceMode mode)
        {
            switch (mode)
            {
                case TraceMode.Shortest:
                    // 只求沿路最短：几乎不加转弯/换类代价，也不专门去搜「外围那条」。
                    return new ModeKnobs { MaxDetourFactor = 1.6, TurnPenalty = 0.08, KindChangePenalty = 1.0,
                        PreferOutermost = false, AllowNetworkSwitch = true, CornerDegrees = 22 };
                case TraceMode.FewestNodes:
                    // 节点最少：只有明显拐点才放点，允许为此用直线多连一段。
                    return new ModeKnobs { MaxDetourFactor = 4.0, TurnPenalty = 0.35, KindChangePenalty = 6.0,
                        PreferOutermost = true, AllowNetworkSwitch = true, CornerDegrees = 40 };
                case TraceMode.FewestCorners:
                    // 交点最少：宁可在同一条路上多走一段，也少在路口拐弯。
                    return new ModeKnobs { MaxDetourFactor = 4.0, TurnPenalty = 1.20, KindChangePenalty = 24.0,
                        PreferOutermost = true, AllowNetworkSwitch = true, CornerDegrees = 22 };
                case TraceMode.NoNetworkSwitch:
                    // 不切换网络：两端各自沿自己那条路走，绝不跳到别的路上。
                    return new ModeKnobs { MaxDetourFactor = 2.0, TurnPenalty = 0.35, KindChangePenalty = 400.0,
                        PreferOutermost = false, AllowNetworkSwitch = false, CornerDegrees = 22 };
                default:
                    return new ModeKnobs { MaxDetourFactor = 3.0, TurnPenalty = 0.35, KindChangePenalty = 6.0,
                        PreferOutermost = true, AllowNetworkSwitch = true, CornerDegrees = 22 };
            }
        }

        /// <summary>
        /// prefabSnapDistance &lt;= 0 时（个别 prefab 没配、或读到默认值）用类别兜底，
        /// 绝不允许半径为 0 —— 半径为 0 会让贴合永远失败，玩家视角是「模组没反应」。
        /// </summary>
        public static double TierFloorRadius(AreaTier tier)
        {
            switch (tier)
            {
                case AreaTier.District: return 40;
                case AreaTier.MapTile: return 30;
                case AreaTier.Space: return 25;
                case AreaTier.Lot: return 12;
                case AreaTier.Surface: return 12;
                default: return 15;
            }
        }

        public static ResolvedTuning Resolve(ModConfig cfg, AreaTier tier, double prefabSnapDistance, double gameMinNodeDistance = 0.0)
        {
            if (cfg == null) cfg = ModConfig.CreateDefault();
            ResolvedTuning r = new ResolvedTuning();
            r.Tier = tier;
            r.Mode = cfg.Mode;
            r.Knobs = ModeKnobs(cfg.Mode);
            r.CurveDetail = GeoKit.Clamp(cfg.CurveDetail, 0, 1);
            r.SnapEnabled = TierEnabled(cfg, tier);
            r.TraceEnabled = ModConfig.TraceAlongNetworks && r.SnapEnabled;
            r.ContinuationEnabled = cfg.NodeContinuation && r.SnapEnabled;

            double baseRadius = prefabSnapDistance > 0.01 ? prefabSnapDistance : TierFloorRadius(tier);
            double radius = baseRadius * SnapDistanceScale(cfg, tier);
            r.SnapRadius = GeoKit.Clamp(radius, cfg.RadiusFloor, cfg.RadiusCeiling);

            // 拐点判定：精细度越高，越小的转角也算「交点」（反馈 4「交点必须放节点」+ 反馈 7 的精细度滑杆）。
            r.CornerDegrees = r.Knobs.CornerDegrees * (1.0 - 0.8 * r.CurveDetail);

            // 弯道密度：旧口径是「相邻两点连线不许越出道路边缘」，它保证严丝合缝但点数不可控；
            // 反馈 4 改成「交点必放、没有交点就不放」，于是这里换成玩家手里的一个滑杆：
            //  · 0（默认）= 允许的弦高 = 路宽的 25%，夹在 1~6 米 ⇒ 大弯道上只留少数折点（会看到轻微切角）；
            //  · 100%     = 允许的弦高 = 路宽的 1%，夹在 0.03~0.5 米 ⇒ 回到上一版那种严丝合缝的密点。
            // 两种都在「交点必放节点」之上工作：滑杆只影响平滑弯道补多少点，动不了交点。
            r.SimplifyTolerance = ChordTolerance(cfg, tier, DEFAULT_ROAD_MODEL_WIDTH);
            r.ChordTol = ChordToleranceBand(tier, r.CurveDetail);
            r.MaxNodesPerEdge = ModConfig.HARD_NODE_CAP;      // 反馈 7：节点上限不再做成设置项
            r.MinSpacing = Math.Max(0.5, cfg.MinAutoNodeSpacing);

            // 游戏自己的最小节点间距是**硬约束**，不是风格问题：
            //   FACT：AreaToolSystem.cs:1577 `minNodeDistance = AreaUtils.GetMinNodeDistance(areaData)`；
            //         :1631 / :1714 在生成区域的 job 里拿它逐对比较相邻节点，太近的边直接判非法；
            //         :1818 提交时比「倒数第二格 → 游标」的距离，小于它这一击被静默吃掉。
            // 所以我们放的自动点，间距一律抬到不小于它；玩家的滑杆只在「比这个更密不要」的下限上起作用。
            // 传 0（读不到，比如个别 prefab 没配）时不动作，退回滑杆值。
            if (gameMinNodeDistance > 0.0) r.MinSpacing = Math.Max(r.MinSpacing, gameMinNodeDistance);

            r.MaxDetourFactor = Math.Max(1.0, r.Knobs.MaxDetourFactor);
            return r;
        }

        /// <summary>「还不知道会用到哪条路」时用的代表路宽（米）：Resolve 阶段只用来给滑杆定标度。</summary>
        public const double DEFAULT_ROAD_MODEL_WIDTH = 20.0;

        /// <summary>
        /// 各类别的候选权重（score = 距离 × 权重，取最小）。
        /// 第五轮口径变化：不再有 <c>PreferAreaBorders</c> 这个开关（反馈 5 删掉了整个「贴合优先级」板块），
        /// 已有区域边界**永远允许吸**，因为反馈 4 明确要求「两个手动节点都贴在另一个专门产业区的边缘上时
        /// 要贴合那个产业区的边缘节点」——那是功能，不是偏好。
        /// </summary>
        public static double KindWeight(ModConfig cfg, SnapKind kind)
        {
            switch (kind)
            {
                // 交叉口：网络接头本身就是一条路上的点，不再单独降权（原「优先交叉口」开关已删）。
                // 但接头仍然值得贴：它天然就是玩家想点的那个角。
                case SnapKind.NetNode: return 0.7;
                case SnapKind.NetCentre: return 1.0;
                // 路缘档在路缘类区域里是**主档**（不是可选项），所以不再压到中心线之后：
                // 0.95 表示「同样近的时候，玩家点的就是地块边该在的地方」。
                case SnapKind.NetSide: return 0.95;
                case SnapKind.ObjectSide: return 1.0;
                case SnapKind.AreaBorderSame: return 0.85;
                case SnapKind.AreaBorderOther: return 1.25;
                case SnapKind.MapTileBorder: return 1.3;
                default: return 4.0;
            }
        }

        /// <summary>
        /// 不带类别的旧口径：**只看总开关**（"这一档整体还在服务范围内吗"）。
        /// 原来它还要读「允许贴路缘线」那个开关，反馈 5 把那个开关删了 ⇒ 现在只剩总开关这一道。
        /// </summary>
        public static bool KindAllowed(ModConfig cfg, SnapKind kind)
        {
            if (cfg == null || !cfg.Enabled) return false;
            return true;
        }

        /// <summary>
        /// 各类别允许出现哪些候选档。这是需求 3 的第一半：**贴合目标按区域类别分档**。
        ///
        /// 口径不自己发明，抄游戏自己那张表
        /// （FACT：AreaToolSystem.cs:3235-3275 <c>GetAvailableSnapMask(AreaGeometryData, bool, out onMask, out offMask)</c>）：
        ///   District → <c>Snap.NetMiddle</c>（道路**中心线**）
        ///   Lot / Surface / Space → <c>Snap.NetSide | Snap.ObjectSide</c>（道路**外沿** + 建筑轮廓）
        ///   MapTile → 两者都不给（游戏压根不让瓦片吸附）
        /// 于是「市辖区贴中心线、产业区/地皮/表面贴路缘」不是我们的偏好，而是游戏自己的分区语义。
        ///
        /// 注意这里没有「路缘拿不到就退回中心线」：反馈 3 明写产业区/表面区贴的是**人行道外缘**，
        /// 把地块角点吸到马路中心去比不吸更错（半条马路会划进地块）。
        /// </summary>
        public static bool KindAllowed(ModConfig cfg, AreaTier tier, SnapKind kind)
        {
            if (cfg == null || !cfg.Enabled) return false;
            bool edgeTier = UsesEdgeTargets(tier);
            switch (kind)
            {
                case SnapKind.Free: return true;
                case SnapKind.NetSide: return edgeTier;
                case SnapKind.NetCentre: return !edgeTier;
                case SnapKind.ObjectSide: return edgeTier;
                case SnapKind.NetNode:
                    // 第六轮反馈 9 收紧：交叉口是**中心线上的点**，所以只在中心线档（市辖区/城市边界）开放。
                    // 老口径在路缘档也保留它，理由是「路口处四条边线交于这一点附近，偏差在米级以下」——
                    // 那句话只在窄路上成立：一条 4 车道 + 人行道的路，中心交点离人行道外缘的角点有十来米，
                    // 把地块角点吸过去就是玩家说的「莫名其妙吸附到另一条路上/鼠标旁边的节点上」（反馈 1）。
                    // 路缘档要的角点本来就有：两侧外沿折线在路口那一段的端点会通过 NetSide 档递上来。
                    return !edgeTier;
                default: return true;
            }
        }

        /// <summary>
        /// 这条网络边对该类别是不是**合法的贴合/描边目标**（第六轮反馈 9 的白名单）。
        ///
        /// 玩家的原话：「除了城市边界，市辖区的节点吸附和贴合路径只限制在道路/轨道中心点，
        /// 不包括步行路、埠头、建筑和道路边缘、建筑内部的道路及其它物体（如摆件、高架路墩、车辆行人等）。
        /// 表面区域的节点吸附和贴合路径和专门产业区一样，只限制在道路/轨道边缘（即两侧人行道边缘，不是路缘）
        /// 和建筑边缘，不包括道路/轨道中心点、建筑内部的道路及其它物体。」
        ///
        /// 三类区域共用的**排除名单**（所以这一档与 tier 无关，参数留着是为了以后能分档）：
        ///  · 只认 <see cref="NetKind.Road"/> 与 <see cref="NetKind.Rail"/> ⇒ 步行路（Path）、
        ///    水道（Waterway）、以及分类不明的 net（Other：护栏、轨道缓冲段之类）一律不贴；
        ///  · <see cref="GraphEdge.OnWater"/> ⇒ 埠头/栈桥不贴；
        ///  · <see cref="GraphEdge.Internal"/> ⇒ 建筑/地块内部的专用道不贴。
        ///
        /// ⚠ 这是**我们自己的口径，不是抄游戏的**：游戏那个 SnapJob 对 net SearchTree 里的边
        /// 只查一件事——组合是不是隧道（FACT：AreaToolSystem.cs:496-508 <c>CheckComposition</c>），
        ///  kinds 一概不过滤，所以原版确实会把市辖区边界吸到一条人行步道上。
        /// 「中心线 vs 外沿」那半才是抄游戏的（FACT：AreaToolSystem.cs:3235-3275 <c>GetAvailableSnapMask</c>）。
        /// </summary>
        public static bool NetAllowed(AreaTier tier, GraphEdge ge)
        {
            return IsTargetNet(ge);
        }

        /// <summary>
        /// 白名单的核心判据（**与类别无关**，三类区域共用同一份排除名单）。
        /// 单独抽出来是因为它不只用于「能不能吸」：<see cref="SnapKit.CrossesObstacle"/> 判
        /// 「这条直线有没有横穿马路」时也必须用同一份名单 —— 否则就出现自相矛盾的形状：
        /// 一条人行步道不许当贴合目标，却能把产业区的边界判成「跨越了道路」而拒绝贴合
        /// （第六轮反馈 10 要求排查的正是这类规则互相打架）。
        ///
        /// 【第八轮反馈 3：地下的一律不算】这一行 <c>Underground</c> 就是那条要求的落点，
        /// 而且它放在**与类别无关**的这一层，不是放在路缘档那一层：
        /// 老口径是「隧道只在路缘档排除，市辖区贴隧道中轴是合理的」，玩家这一轮明确否掉了
        ///（「所有区域工具的节点吸附和自动贴合都不包括建在地下的所有物体」）⇒ 三类区域一起排除。
        /// 判据本身抄游戏：FACT：research/decompiled/Game.Tools/AreaToolSystem.cs:501-508
        /// <c>CheckComposition</c> —— 游戏自己的区域吸附遇到带 <c>CompositionFlags.General.Tunnel</c>
        /// 的组合直接 <c>return false</c>；外加「开槽下沉段」那一半（elevation 低于地面），
        /// 见 <see cref="GraphEdge.Underground"/>。
        /// </summary>
        public static bool IsTargetNet(GraphEdge ge)
        {
            if (ge == null) return false;
            if (ge.Underground) return false;
            if (ge.OnWater) return false;
            if (ge.Internal) return false;
            return ge.Kind == NetKind.Road || ge.Kind == NetKind.Rail;
        }

        /// <summary>
        /// 这个物体能不能当贴合目标：只认**建筑**（反馈 9 的「建筑边缘」），
        /// 摆件/树/高架桥墩/码头桩都不在名单里（车辆与行人本来就不会被抓进来 ——
        /// 采样走的是静态四叉树，FACT：Game.Objects/SearchSystem.cs:419 <c>GetStaticSearchTree</c>）。
        /// 判据本身在 GameSide 填（<see cref="ObjectRef.IsBuilding"/>），这里只做策略。
        /// 第八轮反馈 3 再加一条：**建在地下的物体不是目标**（地下停车场那类）。
        /// 游戏侧没有「这栋楼在地下」这个位（<c>Game.Buildings/BuildingFlags.cs:6-14</c> 里
        /// 只有 HighRentWarning/StreetLightsOff/LowEfficiency/Illuminated/Historical 五个），
        /// 所以这一条用的是物体的落地高程（<c>Game.Objects/Elevation.cs:8-10</c> +
        /// <c>Game.Objects/ObjectUtils.cs:262-286</c> 的 <c>m_Elevation &lt; 0 ⇒ CollisionMask.Underground</c>），
        /// 是我们自己的口径，不是抄游戏的 —— 游戏自己的区域吸附对地下建筑**不加过滤**
        ///（FACT：AreaToolSystem.cs:552-668 的 ObjectIterator 全程没有隧道判据）。
        /// </summary>
        public static bool ObjectAllowed(AreaTier tier, ObjectRef o)
        {
            return o != null && o.IsBuilding && !o.Underground;
        }

        /// <summary>
        /// 描边（A* 与续接走线）能不能踩这条边。判据与 <see cref="NetAllowed"/> 完全一致 ——
        /// 「贴合目标名单」与「描边可走名单」必须是同一份，否则会出现「这一点能吸上去，
        /// 但从这里哪儿都走不了」的死路（第六轮反馈 10 排查的那类自相矛盾）。
        /// 第八轮反馈 3 之后隧道/下沉段已经在 <see cref="IsTargetNet"/> 那一层被排掉了
        ///（老写法只在路缘档排隧道，中心线档放过 —— 玩家这一轮否掉了那个区别）。
        /// 高架仍然**不**排除（第五轮反馈 4 明确要求高架也能贴）。
        /// </summary>
        public static bool TraceEdgeAllowed(AreaTier tier, GraphEdge ge)
        {
            return NetAllowed(tier, ge);
        }

        /// <summary>
        /// 这个图节点（交叉口）值不值得作为吸附候选：**它至少得接着一条合法的网络边**。
        /// 只有步道/内部路交汇的那个点，对三类区域都不是目标 —— 而它恰恰是反馈 1 里
        /// 「空地上莫名其妙吸到鼠标旁边一个节点」的常见来源（人行步道网比车行道密得多）。
        /// </summary>
        public static bool NodeAllowed(AreaTier tier, WorldSnapshot world, int nodeIndex)
        {
            return AllowedEdgeOf(tier, world, nodeIndex) >= 0;
        }

        /// <summary>
        /// 这个图节点上**第一条合法的网络边**（下标），没有就 -1。
        /// 与 <see cref="NodeAllowed"/> 同一份判据：交叉口候选要拿它当描边锚点，
        /// 挑到一条步道上去，描边第一步就走进白名单外的网络了。
        /// </summary>
        public static int AllowedEdgeOf(AreaTier tier, WorldSnapshot world, int nodeIndex)
        {
            if (world == null || nodeIndex < 0 || nodeIndex >= world.Nodes.Count) return -1;
            List<List<WorldSnapshot.AdjEntry>> adj = world.Adjacency;
            if (adj == null || nodeIndex >= adj.Count) return -1;
            List<WorldSnapshot.AdjEntry> list = adj[nodeIndex];
            if (list == null) return -1;
            for (int i = 0; i < list.Count; i++)
            {
                int e = list[i].EdgeIndex;
                if (e < 0 || e >= world.Edges.Count) continue;
                if (NetAllowed(tier, world.Edges[e])) return e;
            }
            return -1;
        }

        /// <summary>
        /// 该类别贴道路**外沿**（并因此可用建筑轮廓）还是贴**中心线**。见 <see cref="KindAllowed(ModConfig,AreaTier,SnapKind)"/> 的游戏出处。
        /// 太空区已删除，但它在游戏的分区表里属于「外沿」那一侧，这里保留语义以免判档漂移。
        /// </summary>
        public static bool UsesEdgeTargets(AreaTier tier)
        {
            switch (tier)
            {
                case AreaTier.Lot:            // 专门产业区、垃圾场填埋区域
                case AreaTier.Surface:        // 表面区域规划工具、地皮表面区域
                case AreaTier.Space:
                    return true;
                case AreaTier.District:       // 市辖区：中心线
                case AreaTier.MapTile:        // 城市边界线：游戏自己连吸附都不给
                default:
                    return false;
            }
        }

        /// <summary>
        /// 「不允许跨越道路或建筑」的严格档。需求 3 把四种路缘类区域分成两档：
        /// 专门产业区 + 垃圾场填埋 = 不许跨越；表面规划 + 地皮表面 = 允许跨越但跨越那段不贴合。
        /// 而游戏里前两种都是 <c>AreaType.Lot</c>、后两种都是 <c>AreaType.Surface</c>
        /// （FACT：AreaType 只有 Lot/District/MapTile/Space/Surface 五个值），
        /// 所以这两档的规则正好落在两个类别上，不必再去分辨具体 prefab。
        /// </summary>
        public static bool BlocksCrossing(AreaTier tier)
        {
            return tier == AreaTier.Lot;
        }

        /// <summary>
        /// 叠置路网的**平局代价**（第六轮反馈 8）：两条路在 XZ 上完全重合（高架正好压在地面路上、
        /// 或者一侧缘线共线）时，它们的贴合距离与描边长度一模一样，谁胜出就完全由遍历顺序决定 ——
        /// 玩家看到的是"同一次点击，这次吸地面路、下次吸高架"。
        /// 玩家给的口径是**按高度最低的那条定节点位置**，所以给每条边加一点点与高度成正比的代价，
        /// 让低的那条严格更便宜。
        ///
        /// 权重取 1e-3（每米高 0.001 米代价）：一条 10 米高的高架只贵 0.01 米，
        /// 远小于任何真实的路线长度差 ⇒ 只在**严格平局**时起作用，不会把本来更短的高架路线挤掉。
        /// 高度取中心线两端的世界高度均值再夹到 ≥0（夹断是为了不引入负权边，Dijkstra 不接受负权）；
        /// 低于海平面的那几条路互相之间不再分高下，但它们本来也不会与高架叠在一起。
        /// </summary>
        public const double HEIGHT_TIE_WEIGHT = 1e-3;

        /// <summary>这条边因为高度而多付的代价（米）。见 <see cref="HEIGHT_TIE_WEIGHT"/>。</summary>
        public static double HeightTieCost(GraphEdge ge)
        {
            if (ge == null || ge.Line == null || ge.Line.Count == 0) return 0.0;
            Polyline l = ge.Line;
            double h = (l.Points[0].H + l.Points[l.Count - 1].H) * 0.5;
            if (double.IsNaN(h) || double.IsInfinity(h)) return 0.0;
            if (h < 0.0) h = 0.0;
            return h * HEIGHT_TIE_WEIGHT;
        }

        /// <summary>
        /// 吸附候选平局时的高度容差（米）：两条重合的路给出的 2D 距离是**严格相等**的
        /// （<see cref="P3.DistanceTo"/> 只看 X/Y），但采样点的浮点噪声可能让它们差几个微米，
        /// 所以按 5 厘米以内算平局，再由「谁更低」定胜负（反馈 8）。
        /// </summary>
        public const double HEIGHT_TIE_EPS = 0.05;

        /// <summary>拿不到路面宽度时 assumed 的最小路宽（米）。玩家指定的值，不要改小。</summary>
        public const double MIN_ASSUMED_ROAD_WIDTH = 3.0;

        /// <summary>
        /// 「相邻两点连线允许切进道路多少米」的容差（米）—— 反馈 4 的「没有交点就不放节点」与
        /// 反馈 7 的「曲线精细度」在这里合流。
        ///
        /// RDP 的容差就是允许的最大弦高，所以这个数直接决定弯道上的节点密度
        /// （弦高 h、曲率半径 R ⇒ 最大弦长 √(8Rh)：弯越急点越密，直路自动只有两个点）。
        /// 精细度 0 ⇒ 路宽的 25%（大弯道只留几个折点）；精细度 1 ⇒ 路宽的 1%（严丝合缝）。
        /// 分档上限按类别收紧：中心线档（市辖区）切进去一点仍在路面上；
        /// 外沿档（产业区/表面区）切出去就是「地块边界压到马路上」，所以上限只有几十厘米。
        /// </summary>
        public static double ChordTolerance(ModConfig cfg, AreaTier tier, double roadWidth)
        {
            return ChordTolerance(cfg, tier, roadWidth, CurveDetailOf(cfg));
        }

        /// <summary>同一件事，但精细度由参数给（<see cref="Resolve"/> 用它，模式/滑杆都算好了）。</summary>
        public static double ChordTolerance(ModConfig cfg, AreaTier tier, double roadWidth, double detail)
        {
            double frac, lo, hi;
            ChordBand(tier, detail, out frac, out lo, out hi);
            double w = roadWidth > 0.01 ? roadWidth : MIN_ASSUMED_ROAD_WIDTH;
            return GeoKit.Clamp(w * frac, lo, hi);
        }

        private static double CurveDetailOf(ModConfig cfg)
        {
            return cfg == null ? 0.0 : GeoKit.Clamp(cfg.CurveDetail, 0, 1);
        }

        /// <summary>
        /// 该类别弦高容差的**上界**（= 这一档允许的最松值）。Resolve 阶段用它，不用
        /// <see cref="ChordTolerance"/> 的「拿不到路宽按 3 米算」兜底：
        /// 「这条路量不出来」才该按最窄路算，而 Resolve 时**还根本不知道会用到哪几条路**，
        /// 那时就取最紧的一档等于把滑杆和每条路的真实宽度一起压死
        /// （回归壳 C9：产业地块一条 314 米的弯被硬塞成 35 个点，滑杆三档全并成一档）。
        /// 真正的收紧点在 TraceKit.ChordHardTolerance —— 那里逐条边按真实路宽取最小。
        /// </summary>
        public static double ChordToleranceBand(AreaTier tier, double detail)
        {
            double frac, lo, hi;
            ChordBand(tier, detail, out frac, out lo, out hi);
            return hi;
        }

        /// <summary>分档参数的唯一出处：比例、下限、上限。精细度滑杆在这里插值。</summary>
        private static void ChordBand(AreaTier tier, double detail, out double frac, out double lo, out double hi)
        {
            double d = GeoKit.Clamp(detail, 0, 1);
            switch (tier)
            {
                // 中心线档（市辖区）：切进去仍然整条弦都在路面上，所以容差最松。
                case AreaTier.District: frac = Lerp(0.25, 0.02, d); lo = Lerp(1.0, 0.05, d); hi = Lerp(6.0, 0.6, d); return;
                // 外沿档（产业区/表面区）：弦就是地块与马路的分界线，切出去多少就露出多少空隙。
                // 上限压到分米级：这条边界一旦切出去，玩家看到的就是「地块吃了半条人行道」。
                case AreaTier.Lot: frac = Lerp(0.08, 0.01, d); lo = Lerp(0.3, 0.05, d); hi = Lerp(1.2, 0.5, d); return;
                case AreaTier.Surface: frac = Lerp(0.10, 0.01, d); lo = Lerp(0.25, 0.03, d); hi = Lerp(1.5, 0.4, d); return;
                case AreaTier.Space: frac = Lerp(0.08, 0.01, d); lo = Lerp(0.3, 0.05, d); hi = Lerp(1.2, 0.5, d); return;
                default: frac = Lerp(0.20, 0.05, d); lo = Lerp(0.6, 0.3, d); hi = Lerp(4.0, 4.0, d); return;
            }
        }

        private static double Lerp(double a, double b, double t) { return a + (b - a) * t; }

        // ————————————————————————————————— 一次绘制的走廊（单调扩张）
        //
        // 【为什么单独有这么一段】第四轮实机的账：fb=NotOnNetwork×121 / resnap=45 / lost=25，而 stale=0。
        // 也就是说锚点没有越界，但**它贴的那条路整个不在新快照里了**。原因是老写法每次都按
        // 「这一段的两个端点」重算走廊：玩家点在远处拐角时，走廊跟着跳过去，**早先那个角所在的那条路被丢出快照**
        // ⇒ 那两条边永远判「不在路上」⇒ 退回直线。玩家看到的就是「有的路会贴、有的不会，很随机」——
        // 决定因素是他这一下点得多远，而不是路本身。
        //
        // 【口径】绘制会话期间走廊只长不缩（单调），且必须始终含住**每一个已放下的角**。
        // 尺寸上限是为了不拿性能换正确性：超限时宁可少贴（并留一行可判读的日志），
        // 也不让每帧去扫半张城（Playbook 点名的卡点）。

        /// <summary>走廊单边的硬上限（米）。超过就按这个尺寸裁，并告诉调用方「被裁了」。</summary>
        public const double STROKE_LIMIT = 2500.0;

        public struct StrokePlan
        {
            /// <summary>本次真正要抓的框（已外扩、已按上限裁过）。无效表示什么都不做。</summary>
            public Box2 Capture;
            /// <summary>下一次传回来的「原始并集框」（不含外扩）。</summary>
            public Box2 Raw;
            /// <summary>被上限裁过 ⇒ 调用方要打一行日志（这是尺寸边界，不是 bug）。</summary>
            public bool Clamped;
        }

        /// <summary>
        /// 规划这一次抓多大。参数都是「本帧才知道的」：已有锚点、本段两端、这一档的贴合半径、上一帧的原始框。
        /// <paramref name="previousRaw"/> 无效（新会话第一笔）时按当次起算。
        /// </summary>
        public static StrokePlan PlanStroke(IList<P3> anchors, P3 a, P3 b, double radius, Box2 previousRaw)
        {
            Box2 raw = previousRaw;
            raw = raw.Union(Box2Of(a));
            raw = raw.Union(Box2Of(b));
            if (anchors != null)
            {
                for (int i = 0; i < anchors.Count; i++) raw = raw.Union(Box2Of(anchors[i]));
            }

            StrokePlan plan = new StrokePlan();
            plan.Raw = raw;
            if (!raw.IsValid) { plan.Capture = Box2.Empty(); return plan; }

            // 外扩量：至少一个半径（锚点要能吸到它两侧的路），并按整圈跨度补一点，
            // 让「相邻两个角之间那条沿路弯出去的线」也落在框里 —— 描边走的是走廊，不是弦。
            double inflate = Math.Max(radius * 1.5, radius + raw.MaxSide * 0.15);
            Box2 cap = raw.Inflated(inflate);

            double side = cap.MaxSide;
            if (side > STROKE_LIMIT)
            {
                // 以框中心为锚裁到上限：保持单调性对「已经收进来的角」尽量友好
                // （真超出半个城市时本来也就不该指望一次描边把全城扫一遍）。
                double shrink = (side - STROKE_LIMIT) * 0.5;
                double cx = (cap.MinX + cap.MaxX) * 0.5;
                double cy = (cap.MinY + cap.MaxY) * 0.5;
                double halfX = Math.Max(1.0, (cap.MaxX - cap.MinX) * 0.5 - shrink);
                double halfY = Math.Max(1.0, (cap.MaxY - cap.MinY) * 0.5 - shrink);
                cap = new Box2 { MinX = cx - halfX, MinY = cy - halfY, MaxX = cx + halfX, MaxY = cy + halfY };
                plan.Clamped = true;
            }
            plan.Capture = cap;
            return plan;
        }

        private static Box2 Box2Of(P3 p)
        {
            Box2 b = Box2.Empty();
            b.Expand(p);
            return b;
        }
    }
}

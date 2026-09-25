using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// PolicyKit：第五轮反馈 3/5/7 的全部数值口径。
        ///
        /// 这一页在第五轮被整个换掉了，所以断言也跟着重写的。旧口径是
        /// 「统一倍率 × 类别额外倍率 × 行政区再加一档」三层相乘 —— 玩家看不出自己调的到底是什么
        /// （反馈 3 原话：不要统一调，按三类各自单独设置）。新口径只有一层：
        /// **每类一条「节点吸附距离」滑杆，乘在游戏 prefab 自己的 m_SnapDistance 上**
        /// （设计文档 §一 第 6 条），所以这里断言的是倍率关系与夹取，不是具体米数。
        ///
        /// 另外三组是本轮回来的新面：
        ///  · O5 贴合模式 → 五组具体数字（反馈 5 用下拉框取代了整个「贴合优先级」板块）；
        ///  · O6 曲线精细度 → 交点阈值与弦高容差（反馈 4「交点必放节点」+ 反馈 7「精细度默认 0」）；
        ///  · O7 每类允许的贴合目标档位（反馈 3「市辖区吸中心线、产业区/表面区吸人行道外缘」）。
        /// </summary>
        internal static class Policy
        {
            public static void Run()
            {
                Harness.Section("PolicyKit 节点吸附距离按类分档（O，反馈 3）", () =>
                {
                    var cfg = ModConfig.CreateDefault();

                    // 默认 100% = 原版距离：四档全是 1.0，且 Resolve 出来的半径就等于 prefab 本身。
                    Check.Close("O 默认倍率：市辖区 1.0", 1.0, PolicyKit.SnapDistanceScale(cfg, AreaTier.District), 1e-12);
                    Check.Close("O 默认倍率：专门产业区 1.0", 1.0, PolicyKit.SnapDistanceScale(cfg, AreaTier.Lot), 1e-12);
                    Check.Close("O 默认倍率：表面区域 1.0", 1.0, PolicyKit.SnapDistanceScale(cfg, AreaTier.Surface), 1e-12);
                    Check.Close("O 默认倍率：城市边界线 1.0", 1.0, PolicyKit.SnapDistanceScale(cfg, AreaTier.MapTile), 1e-12);
                    Check.Close("O 100% ⇒ 半径正好等于 prefab（不掺任何隐藏加成）", 20.0,
                        PolicyKit.Resolve(cfg, AreaTier.District, 20).SnapRadius, 1e-9);
                    Check.Close("O 认不出的类别 ⇒ 倍率兜底 1.0", 1.0, PolicyKit.SnapDistanceScale(cfg, AreaTier.Other), 1e-12);
                    Check.Close("O cfg=null ⇒ 倍率兜底 1.0", 1.0, PolicyKit.SnapDistanceScale(null, AreaTier.District), 1e-12);

                    // 旧需求 7 的「行政区范围比产业区大」在第五轮被玩家自己接管了：
                    // 模组不再自作主张拉开差距，改由玩家调三条独立滑杆。
                    Check.True("O 同一 prefab、同一默认值 ⇒ 市辖区与产业区半径相等（不再自动分级）", () =>
                        Harness.Near(PolicyKit.Resolve(cfg, AreaTier.District, 20).SnapRadius,
                                     PolicyKit.Resolve(cfg, AreaTier.Lot, 20).SnapRadius, 1e-9));

                    // 各调各的：动一档不许牵连别档（反馈 3「每类单独设置」的全部意义）。
                    Check.True("O 只调市辖区 ⇒ 只有市辖区变（其余三档不受牵连）", () =>
                    {
                        var c = ModConfig.CreateDefault();
                        c.DistrictSnapDistance = 2.5;
                        double d = PolicyKit.Resolve(c, AreaTier.District, 20).SnapRadius;
                        double l = PolicyKit.Resolve(c, AreaTier.Lot, 20).SnapRadius;
                        double s = PolicyKit.Resolve(c, AreaTier.Surface, 20).SnapRadius;
                        double m = PolicyKit.Resolve(c, AreaTier.MapTile, 20).SnapRadius;
                        return Harness.Near(d, 50.0, 1e-9) && Harness.Near(l, 20.0, 1e-9)
                            && Harness.Near(s, 20.0, 1e-9) && Harness.Near(m, 20.0, 1e-9);
                    });
                    Check.True("O 只调产业区 ⇒ 只有产业区变", () =>
                    {
                        var c = ModConfig.CreateDefault();
                        c.LotSnapDistance = 0.6;
                        return Harness.Near(PolicyKit.Resolve(c, AreaTier.Lot, 20).SnapRadius, 12.0, 1e-9)
                            && Harness.Near(PolicyKit.Resolve(c, AreaTier.District, 20).SnapRadius, 20.0, 1e-9);
                    });

                    // 滑杆区间 50%~300%（选项页把滑杆卡在这个区间，这里断言区间内的行为是单调线性的）
                    double prevD = 0, prevL = 0;
                    bool mono = true, lin = true;
                    foreach (double pct in new double[] { 0.5, 1.0, 1.5, 2.0, 3.0 })
                    {
                        var c = ModConfig.CreateDefault();
                        c.DistrictSnapDistance = pct;
                        c.LotSnapDistance = pct;
                        double rd = PolicyKit.Resolve(c, AreaTier.District, 20).SnapRadius;
                        double rl = PolicyKit.Resolve(c, AreaTier.Lot, 20).SnapRadius;
                        if (prevD > 0 && !(rd > prevD)) mono = false;
                        if (prevL > 0 && !(rl > prevL)) mono = false;
                        if (!Harness.Near(rd, 20.0 * pct, 1e-9)) lin = false;   // 只乘一层，没有第二档藏在里面
                        prevD = rd; prevL = rl;
                    }
                    Check.True("O 滑杆在 50%~300% 内 ⇒ 半径单调变大", () => mono);
                    Check.True("O 滑杆读数就是倍率本身（半径 = prefab × 百分比，无叠加项）", () => lin);
                    Check.True("O prefab 越大半径越大（未触及上下限）", () =>
                        PolicyKit.Resolve(cfg, AreaTier.Lot, 10).SnapRadius < PolicyKit.Resolve(cfg, AreaTier.Lot, 30).SnapRadius);
                });

                Harness.Section("PolicyKit 上下限与兜底（O2）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    Check.Close("O2 上下限就是 ModConfig 那两个字段", 8.0, cfg.RadiusFloor, 1e-12);
                    Check.Close("O2 上限 260 米", 260.0, cfg.RadiusCeiling, 1e-12);
                    Check.Close("O2 prefab 极大 ⇒ 夹到上限（大区域在旷野里也不会被一公里外的路拽走）",
                        260, PolicyKit.Resolve(cfg, AreaTier.District, 1e6).SnapRadius, 1e-9);
                    Check.Close("O2 prefab 极小 ⇒ 夹到下限", 8, PolicyKit.Resolve(cfg, AreaTier.Lot, 0.02).SnapRadius, 1e-9);
                    Check.Close("O2 prefab=0 ⇒ 用类别兜底半径（Lot 12）×倍率 1", 12, PolicyKit.Resolve(cfg, AreaTier.Lot, 0).SnapRadius, 1e-9);
                    Check.Close("O2 prefab<0 ⇒ 同样兜底，绝不给负数", 12, PolicyKit.Resolve(cfg, AreaTier.Lot, -50).SnapRadius, 1e-9);
                    Check.Close("O2 prefab=NaN ⇒ 兜底而不是把 NaN 传下去", 12, PolicyKit.Resolve(cfg, AreaTier.Lot, double.NaN).SnapRadius, 1e-9);
                    Check.True("O2 市辖区兜底比产业区大（游戏的量级本来就这样：District 32 米 vs Lot 8 米）", () =>
                        PolicyKit.TierFloorRadius(AreaTier.District) > PolicyKit.TierFloorRadius(AreaTier.Lot));
                    Check.True("O2 每个类别都有正的兜底半径", () =>
                    {
                        foreach (AreaTier t in Enum.GetValues(typeof(AreaTier)))
                            if (!(PolicyKit.TierFloorRadius(t) > 0)) return false;
                        return true;
                    });

                    // 「绝不允许半径为 0」：半径为 0 时玩家视角就是「模组没反应」（本文件注释自己写的话）
                    int n = 0; bool allPos = true; string first = null;
                    double[] prefabs = { 0, -1, 0.001, 0.009, 5, 20, 1e6 };
                    double[] scales = { 0, 0.0001, 0.5, 1, 3, 100, double.NaN, double.NegativeInfinity };
                    foreach (double p in prefabs)
                    {
                        foreach (double m in scales)
                        {
                            foreach (AreaTier t in new[] { AreaTier.District, AreaTier.Lot, AreaTier.Surface, AreaTier.MapTile, AreaTier.Space, AreaTier.Other })
                            {
                                var c = ModConfig.CreateDefault();
                                c.DistrictSnapDistance = m; c.LotSnapDistance = m;
                                c.SurfaceSnapDistance = m; c.MapTileSnapDistance = m;
                                var r = PolicyKit.Resolve(c, t, p);
                                n++;
                                if (!(r.SnapRadius > 0) || double.IsNaN(r.SnapRadius) || double.IsInfinity(r.SnapRadius))
                                {
                                    allPos = false;
                                    if (first == null) first = "prefab=" + p + " scale=" + m + " tier=" + t + " radius=" + r.SnapRadius;
                                }
                            }
                        }
                    }
                    Check.True("O2 " + n + " 组极端参数下 SnapRadius 恒为正有限值（首例外：" + (first ?? "无") + "）", () => allPos);
                    // 脏配置探针：玩家的存档里真可能出现 NaN（旧版本写盘、手写配置文件）。
                    // 策略层必须自己免疫，不能指望 StoreKit 已经把过一道关。
                    var nanScale = PolicyKit.Resolve(Scale(AreaTier.Lot, double.NaN), AreaTier.Lot, 20);
                    Check.True("O2 倍率=NaN 时半径仍须是有限正数（Clamp 会把它消毒成下限）", () =>
                        !double.IsNaN(nanScale.SnapRadius) && nanScale.SnapRadius > 0);
                    Check.Guard("O2 cfg=null 不抛", () =>
                    {
                        var r = PolicyKit.Resolve(null, AreaTier.District, 20);
                        Check.True("O2 cfg=null 仍能给出正半径", () => r.SnapRadius > 0);
                    });
                    var other = PolicyKit.Resolve(cfg, AreaTier.Other, 20);
                    Check.True("O2 未知类别也有可用半径", () => other.SnapRadius > 0);
                    Check.True("O2 上下限反着填也不炸（夹取区间为空时取下限）", () =>
                    {
                        var c = ModConfig.CreateDefault();
                        c.RadiusFloor = 300; c.RadiusCeiling = 10;
                        double v = PolicyKit.Resolve(c, AreaTier.District, 20).SnapRadius;
                        return !double.IsNaN(v) && !double.IsInfinity(v);
                    });
                });

                Harness.Section("PolicyKit 开关与派生量（O3，反馈 2/6/7）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    Check.True("O3 默认总开关是开的（反馈 2：不要让玩家装完模组还得先开一次）", () => cfg.Enabled);
                    Check.Bool("O3 默认市辖区开", true, PolicyKit.TierEnabled(cfg, AreaTier.District));
                    Check.Bool("O3 默认专门产业区开", true, PolicyKit.TierEnabled(cfg, AreaTier.Lot));
                    Check.Bool("O3 默认表面区域开", true, PolicyKit.TierEnabled(cfg, AreaTier.Surface));
                    Check.Bool("O3 城市边界线属于「特殊吸附目标」，默认关", false, PolicyKit.TierEnabled(cfg, AreaTier.MapTile));
                    Check.Bool("O3 太空区已按反馈 6 整条删除 ⇒ 永不介入", false, PolicyKit.TierEnabled(cfg, AreaTier.Space));
                    Check.Bool("O3 认不出类别时不介入", false, PolicyKit.TierEnabled(cfg, AreaTier.Other));

                    var master = ModConfig.CreateDefault();
                    master.Enabled = false;
                    Check.True("O3 总开关关掉 ⇒ 所有类别都关（需求 2 的「一键退回原版」）", () =>
                    {
                        foreach (AreaTier t in Enum.GetValues(typeof(AreaTier)))
                            if (PolicyKit.TierEnabled(master, t)) return false;
                        return true;
                    });
                    var oneOff = ModConfig.CreateDefault();
                    oneOff.SnapLot = false;
                    Check.Bool("O3 单独关掉专门产业区 ⇒ 该档关", false, PolicyKit.TierEnabled(oneOff, AreaTier.Lot));
                    Check.Bool("O3 单独关掉专门产业区 ⇒ 市辖区不受牵连", true, PolicyKit.TierEnabled(oneOff, AreaTier.District));
                    Check.Bool("O3 关掉后 Resolve 给出 SnapEnabled=false", false, PolicyKit.Resolve(oneOff, AreaTier.Lot, 20).SnapEnabled);
                    Check.Bool("O3 关掉后 TraceEnabled 也跟着关（描边不能脱离贴合单独活）", false, PolicyKit.Resolve(oneOff, AreaTier.Lot, 20).TraceEnabled);
                    Check.Bool("O3 关掉后节点续接也跟着关", false, PolicyKit.Resolve(oneOff, AreaTier.Lot, 20).ContinuationEnabled);

                    // 反馈 7：描边不再是设置项（「都默认允许，不要显示了」）⇒ 它必须是常量 true。
                    Check.Bool("O3 沿网络描边恒为开（已从选项页删除）", true, ModConfig.TraceAlongNetworks);
                    Check.Bool("O3 跨网络类型描边恒为开", true, ModConfig.TraceAcrossNetworkKinds);
                    Check.Bool("O3 环形道路绕行恒为开", true, ModConfig.TraceAroundRings);
                    Check.Bool("O3 于是开着贴合就一定开着描边", true,
                        PolicyKit.Resolve(cfg, AreaTier.District, 20).TraceEnabled);
                    Check.Int("O3 单条边节点上限是硬保险 4000（反馈 7：玩家侧「无限」）", 4000, ModConfig.HARD_NODE_CAP);
                    Check.Int("O3 Resolve 用的就是这个硬保险，不再有玩家可见的节点上限", ModConfig.HARD_NODE_CAP,
                        PolicyKit.Resolve(cfg, AreaTier.Lot, 20).MaxNodesPerEdge);

                    // 节点续接（反馈 7 新增，取代「自由点之间走弧线」）：默认开，且总开关关掉时一起关。
                    Check.Bool("O3 节点续接默认开", true, cfg.NodeContinuation);
                    Check.Bool("O3 续接跟着这一档的开关走", false, PolicyKit.Resolve(master, AreaTier.District, 20).ContinuationEnabled);
                    var noCont = ModConfig.CreateDefault();
                    noCont.NodeContinuation = false;
                    Check.Bool("O3 单独关掉续接 ⇒ 贴合与描边照旧", true,
                        PolicyKit.Resolve(noCont, AreaTier.District, 20).TraceEnabled);
                    Check.Bool("O3 单独关掉续接 ⇒ 只有续接关", false,
                        PolicyKit.Resolve(noCont, AreaTier.District, 20).ContinuationEnabled);

                    Check.True("O3 破配置（间距负数）也不能让派生量瘫掉", () =>
                    {
                        var broken = new ModConfig { MinAutoNodeSpacing = -5 };
                        broken.DistrictSnapDistance = double.NaN;
                        var r = PolicyKit.Resolve(broken, AreaTier.District, 20);
                        return r.MinSpacing >= 0.5 && r.MaxDetourFactor >= 1.0
                            && !double.IsNaN(r.SimplifyTolerance) && r.SimplifyTolerance > 0
                            && !double.IsNaN(r.CornerDegrees) && r.CornerDegrees > 0;
                    });
                });

                Harness.Section("PolicyKit 游戏最小节点间距（O4，需求 7 的硬下界）", () =>
                {
                    // 游戏自己的量（FACT：Game.Areas.AreaUtils）：
                    //   GetMinNodeDistance(areaData) = m_SnapDistance * 0.5；类别表 Lot 8 / District 32 /
                    //   MapTile 64 / Space 1 / Surface 0.75。区域生成的 job 与提交判定都拿它比相邻节点距离
                    //   （AreaToolSystem.cs:1577/1631/1714/1818）⇒ 我们的自动点比它近就是给游戏添非法几何。
                    var cfg = ModConfig.CreateDefault();
                    var noFloor = PolicyKit.Resolve(cfg, AreaTier.District, 64, 0);
                    var withFloor = PolicyKit.Resolve(cfg, AreaTier.District, 64, 32);
                    Check.Close("O4 传 0（读不到）⇒ 仍按玩家滑杆，不做多余的事", 2.0, noFloor.MinSpacing, 1e-9);
                    Check.True("O4 游戏间距更大时以游戏为准（我们不许比它更密）", () => withFloor.MinSpacing >= 32.0 - 1e-9);
                    Check.True("O4 游戏间距很小时不降级玩家的滑杆值（Surface 0.75 米不该把 2 米的下限压没）", () =>
                    {
                        var s = PolicyKit.Resolve(cfg, AreaTier.Surface, 2, 0.75);
                        return s.MinSpacing >= cfg.MinAutoNodeSpacing - 1e-9;
                    });
                    Check.True("O4 负数/NaN 的读取结果一律忽略", () =>
                    {
                        var a = PolicyKit.Resolve(cfg, AreaTier.Lot, 12, -5);
                        var b = PolicyKit.Resolve(cfg, AreaTier.Lot, 12, double.NaN);
                        return Math.Abs(a.MinSpacing - PolicyKit.Resolve(cfg, AreaTier.Lot, 12, 0).MinSpacing) < 1e-9
                            && Math.Abs(b.MinSpacing - PolicyKit.Resolve(cfg, AreaTier.Lot, 12, 0).MinSpacing) < 1e-9;
                    });
                    Check.True("O4 抬高 MinSpacing 之后仍不破坏「描边点离端点至少 MinSpacing」这条不变式", () =>
                    {
                        var t = PolicyKit.Resolve(cfg, AreaTier.District, 64, 40);
                        var w = Fix.OneStraightEdge(100, 10);
                        var a = Fix.OnEdge(Fix.XY(5, 0), 0, 5);
                        var b = Fix.OnEdge(Fix.XY(95, 0), 0, 95);
                        var r = TraceKit.Trace(a, b, w, cfg, t, Fix.EmptyCtx());
                        for (int i = 0; i < r.Points.Count; i++)
                        {
                            if (r.Points[i].DistanceTo(a.Pos) < t.MinSpacing - 1e-9) return false;
                            if (r.Points[i].DistanceTo(b.Pos) < t.MinSpacing - 1e-9) return false;
                        }
                        return true;
                    });
                });

                Harness.Section("PolicyKit 贴合模式（O5，反馈 5：取代「贴合优先级」板块）", () =>
                {
                    TraceMode[] modes = (TraceMode[])Enum.GetValues(typeof(TraceMode));
                    Check.Int("O5 五个模式（智能 + 四个取向）", 5, modes.Length);
                    Check.Int("O5 默认模式必须是 Smart（反馈 5：默认最智能）", 0, (int)ModConfig.CreateDefault().Mode);

                    Check.True("O5 每个模式的旋钮都在合法区间内", () =>
                    {
                        foreach (TraceMode m in modes)
                        {
                            ModeKnobs k = PolicyKit.ModeKnobs(m);
                            if (!(k.MaxDetourFactor >= 1.0)) return false;         // <1 会让任何绕行都不成立
                            if (!(k.TurnPenalty > 0.0)) return false;
                            if (!(k.KindChangePenalty > 0.0)) return false;
                            if (!(k.CornerDegrees > 0.0 && k.CornerDegrees < 120.0)) return false;
                        }
                        return true;
                    });
                    Check.True("O5 模式之间不许互相串味：五组旋钮不可能完全相同", () =>
                    {
                        for (int i = 0; i < modes.Length; i++)
                        {
                            for (int j = i + 1; j < modes.Length; j++)
                                if (Same(PolicyKit.ModeKnobs(modes[i]), PolicyKit.ModeKnobs(modes[j]))) return false;
                        }
                        return true;
                    });

                    var smart = PolicyKit.ModeKnobs(TraceMode.Smart);
                    var shortK = PolicyKit.ModeKnobs(TraceMode.Shortest);
                    var fewN = PolicyKit.ModeKnobs(TraceMode.FewestNodes);
                    var fewC = PolicyKit.ModeKnobs(TraceMode.FewestCorners);
                    var noSw = PolicyKit.ModeKnobs(TraceMode.NoNetworkSwitch);

                    // 「路径最短」：几乎不加转弯/换类代价，绕行上限也最紧，且不专门去搜最外围那条。
                    Check.True("O5 路径最短 ⇒ 转弯代价最低、换类代价最低、绕行上限最紧", () =>
                        shortK.TurnPenalty < smart.TurnPenalty && shortK.KindChangePenalty < smart.KindChangePenalty
                        && shortK.MaxDetourFactor < smart.MaxDetourFactor);
                    Check.Bool("O5 路径最短 ⇒ 不为「外围」多搜一遍（那要走更长的路）", false, shortK.PreferOutermost);

                    // 「节点最少」：只有明显拐点才放点 ⇒ 拐点判定最宽。
                    Check.True("O5 节点最少 ⇒ 拐点判定比其他模式都宽（角度不够大就不算交点、不放点）", () =>
                    {
                        foreach (TraceMode m in modes)
                            if (m != TraceMode.FewestNodes && !(fewN.CornerDegrees > PolicyKit.ModeKnobs(m).CornerDegrees)) return false;
                        return true;
                    });

                    // 「交点最少」：宁可在同一条路上多走一段，也少拐弯 ⇒ 转弯与换类代价最高。
                    Check.True("O5 交点最少 ⇒ 转弯代价最高、换类代价高于智能", () =>
                        fewC.TurnPenalty > smart.TurnPenalty && fewC.KindChangePenalty > smart.KindChangePenalty);
                    Check.True("O5 交点最少 ⇒ 愿意为此多绕（绕行上限不小于智能）", () =>
                        fewC.MaxDetourFactor >= smart.MaxDetourFactor);

                    // 「不切换网络」：唯一关掉切换的模式。
                    Check.Bool("O5 不切换网络 ⇒ AllowNetworkSwitch=false", false, noSw.AllowNetworkSwitch);
                    Check.True("O5 其余模式都允许切换（反馈 4：切换处必须放节点，但要有得切）", () =>
                    {
                        foreach (TraceMode m in modes)
                        {
                            if (m == TraceMode.NoNetworkSwitch) continue;
                            if (!PolicyKit.ModeKnobs(m).AllowNetworkSwitch) return false;
                        }
                        return true;
                    });
                    Check.Bool("O5 不切换网络 ⇒ 不绕最外围（本来就只沿自己那条走）", false, noSw.PreferOutermost);
                    Check.Bool("O5 智能/节点最少/交点最少 ⇒ 允许为「包住街区外侧」各搜一遍", true,
                        smart.PreferOutermost && fewN.PreferOutermost && fewC.PreferOutermost);

                    // 旋钮必须真的流到 Resolve：只看旋钮不看界面状态是这层存在的全部理由。
                    Check.True("O5 每个模式的 Resolve 都拿到自己那一组旋钮（含 MaxDetourFactor≥1 的保护）", () =>
                    {
                        foreach (TraceMode m in modes)
                        {
                            var c = ModConfig.CreateDefault();
                            c.Mode = m;
                            var r = PolicyKit.Resolve(c, AreaTier.District, 20);
                            ModeKnobs k = PolicyKit.ModeKnobs(m);
                            if (!r.Mode.Equals(m)) return false;
                            if (!Same(r.Knobs, k)) return false;
                            if (!Harness.Near(r.MaxDetourFactor, Math.Max(1.0, k.MaxDetourFactor), 1e-12)) return false;
                            if (!Harness.Near(r.CornerDegrees, k.CornerDegrees, 1e-12)) return false;
                        }
                        return true;
                    });
                    Check.True("O5 换模式不许动节点吸附距离（模式管选路，不管能吸多远）", () =>
                    {
                        double first = PolicyKit.Resolve(Mode(TraceMode.Smart), AreaTier.Lot, 20).SnapRadius;
                        foreach (TraceMode m in modes)
                            if (!Harness.Near(PolicyKit.Resolve(Mode(m), AreaTier.Lot, 20).SnapRadius, first, 1e-12)) return false;
                        return true;
                    });
                    Check.True("O5 认不出的模式值（游戏以后加第 6 个）⇒ 退回智能那一组", () =>
                        Same(PolicyKit.ModeKnobs((TraceMode)999), smart));

                    // 已有区域边界**永远**允许吸（反馈 4 要的是功能，不是偏好；反馈 5 把开关删了）。
                    Check.True("O5 任何模式下都不许把「贴已有区域边界」这条能力关掉", () =>
                    {
                        foreach (TraceMode m in modes)
                        {
                            var c = ModConfig.CreateDefault();
                            c.Mode = m;
                            if (!PolicyKit.KindAllowed(c, AreaTier.Lot, SnapKind.AreaBorderSame)) return false;
                            if (!PolicyKit.KindAllowed(c, AreaTier.District, SnapKind.AreaBorderSame)) return false;
                            if (!PolicyKit.KindAllowed(c, AreaTier.Lot, SnapKind.AreaBorderOther)) return false;
                        }
                        return true;
                    });
                });

                Harness.Section("PolicyKit 曲线精细度与交点阈值（O6，反馈 4 + 反馈 7）", () =>
                {
                    var cfg = ModConfig.CreateDefault();
                    Check.Close("O6 曲线精细度默认 0（反馈 7：只在交点放节点）", 0.0, cfg.CurveDetail, 1e-12);

                    var d0 = PolicyKit.Resolve(cfg, AreaTier.District, 20);
                    var d50 = PolicyKit.Resolve(Detail(0.5), AreaTier.District, 20);
                    var d100 = PolicyKit.Resolve(Detail(1.0), AreaTier.District, 20);

                    Check.True("O6 精细度越高 ⇒ 交点判定越严（越小的转角也算交点、要放节点）", () =>
                        d0.CornerDegrees > d50.CornerDegrees && d50.CornerDegrees > d100.CornerDegrees);
                    Check.Close("O6 精细度 100% ⇒ 拐点阈值收到基准的 20%", d0.CornerDegrees * 0.2, d100.CornerDegrees, 1e-9);
                    Check.True("O6 精细度再高也不许把阈值收到 0（0 等于「处处是交点」⇒ 每格都放点）", () =>
                        PolicyKit.Resolve(Detail(1.0), AreaTier.Lot, 20).CornerDegrees > 0.5);

                    Check.True("O6 精细度越高 ⇒ 弦高容差越小（弯道补更多折点）", () =>
                        d0.SimplifyTolerance > d50.SimplifyTolerance && d50.SimplifyTolerance > d100.SimplifyTolerance);
                    Check.True("O6 精细度越界（-3 / +9）夹到 [0,1]", () =>
                        Harness.Near(PolicyKit.Resolve(Detail(-3), AreaTier.District, 20).SimplifyTolerance, d0.SimplifyTolerance, 1e-12)
                        && Harness.Near(PolicyKit.Resolve(Detail(9), AreaTier.District, 20).SimplifyTolerance, d100.SimplifyTolerance, 1e-12));
                    Check.True("O6 精细度=NaN ⇒ 容差仍是有限正数（Clamp 消毒成 0 档）", () =>
                    {
                        double v = PolicyKit.Resolve(Detail(double.NaN), AreaTier.Lot, 20).SimplifyTolerance;
                        return !double.IsNaN(v) && v > 0;
                    });

                    // 硬界（ChordToleranceBand）是「这一档最松也不许超过」：Resolve 阶段还不知道会用到哪条路，
                    // 所以取的是区间上界，而不是按最窄路算出来的紧值（回归壳 C9 就是被这条坑过）。
                    Check.True("O6 硬界 ≥ 滑杆容差（Resolve 阶段不许提前把滑杆和路宽一起压死）", () =>
                    {
                        foreach (AreaTier t in new[] { AreaTier.District, AreaTier.Lot, AreaTier.Surface })
                        {
                            var r = PolicyKit.Resolve(Detail(0.5), t, 20);
                            if (!(r.ChordTol >= r.SimplifyTolerance - 1e-12)) return false;
                        }
                        return true;
                    });
                    Check.True("O6 硬界随精细度单调收紧", () =>
                        PolicyKit.ChordToleranceBand(AreaTier.District, 0) > PolicyKit.ChordToleranceBand(AreaTier.District, 0.5)
                        && PolicyKit.ChordToleranceBand(AreaTier.District, 0.5) > PolicyKit.ChordToleranceBand(AreaTier.District, 1));
                    Check.True("O6 外沿档的硬界必须比中心线档严（切出去就是地块吃了人行道）", () =>
                        PolicyKit.ChordToleranceBand(AreaTier.District, 0) > PolicyKit.ChordToleranceBand(AreaTier.Lot, 0));
                    Check.True("O6 认不出的类别也有正的硬界", () => PolicyKit.ChordToleranceBand(AreaTier.Other, 0.5) > 0);

                    // 同一路宽下：容差 = 路宽 × 比例，再夹到该档区间。
                    // 取 12/18 米这一段：市辖区精细度 0 档的比例是 25%，夹取区间 [1,6] ⇒
                    // 线性区间是路宽 4~24 米；拿 40/60 米去比会双双撞到 6 米上限，量不出比例关系。
                    Check.True("O6 容差随路宽线性变化（未被上下限夹住时）", () =>
                    {
                        double a = PolicyKit.ChordTolerance(cfg, AreaTier.District, 12);
                        double b = PolicyKit.ChordTolerance(cfg, AreaTier.District, 18);
                        return b > a && Harness.Near(b / a, 1.5, 1e-9);
                    });
                    Check.True("O6 路宽读不到 ⇒ 按最窄路兜底，绝不给 0", () =>
                    {
                        double z = PolicyKit.ChordTolerance(cfg, AreaTier.District, 0);
                        double neg = PolicyKit.ChordTolerance(cfg, AreaTier.District, -25);
                        return z > 0 && neg > 0 && Harness.Near(z, neg, 1e-12);
                    });
                    Check.True("O6 极窄路上仍不小于该档下限", () =>
                        PolicyKit.ChordTolerance(cfg, AreaTier.Lot, 0.5) >= 0.3 - 1e-12);
                });

                Harness.Section("PolicyKit 每类允许的贴合目标档位（O7，反馈 3）", () =>
                {
                    var cfg = ModConfig.CreateDefault();

                    // 抄的是游戏自己那张表（FACT：AreaToolSystem.cs:3235-3275 GetAvailableSnapMask）：
                    //   District → Snap.NetMiddle（中心线）；Lot/Surface/Space → NetSide|ObjectSide（外沿）。
                    Check.True("O7 市辖区：只吸道路/轨道中心线，不吸路缘", () =>
                        PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.NetCentre)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.NetSide)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.ObjectSide));
                    Check.True("O7 专门产业区：吸人行道外缘与建筑轮廓，不吸中心线（吸中心=把半条马路划进地块）", () =>
                        PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetSide)
                        && PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.ObjectSide)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetCentre));
                    Check.True("O7 表面区域：与专门产业区同档（游戏的 SnapMask 也是同一档）", () =>
                        PolicyKit.KindAllowed(cfg, AreaTier.Surface, SnapKind.NetSide)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.Surface, SnapKind.NetCentre)
                        && PolicyKit.UsesEdgeTargets(AreaTier.Surface) == PolicyKit.UsesEdgeTargets(AreaTier.Lot));
                    Check.Bool("O7 太空区按游戏的分区语义属于外沿档（虽然本模组已不再介入该工具）", true,
                        PolicyKit.UsesEdgeTargets(AreaTier.Space));
                    Check.Bool("O7 城市边界线属于中心线档（游戏压根不让瓦片吸附，这里只是别把档判漂）", false,
                        PolicyKit.UsesEdgeTargets(AreaTier.MapTile));

                    // 第六轮反馈 9 改了口径：交叉口是**中心线上的点**，所以只在中心线档开放。
                    // 路缘档（产业区/表面区）吸路口中心 = 把地块角点放到马路中间，
                    // 4 车道的路上偏差有十来米，正是玩家说的「莫名其妙吸附到另一条路上」。
                    Check.True("O7 交叉口只在中心线档开放（反馈 9：路缘档不吸中心线上的点）", () =>
                        PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.NetNode)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.NetNode)
                        && !PolicyKit.KindAllowed(cfg, AreaTier.Surface, SnapKind.NetNode));
                    Check.True("O7 「什么都不吸」永远合法（玩家点旷野就是自由点）", () =>
                        PolicyKit.KindAllowed(cfg, AreaTier.District, SnapKind.Free)
                        && PolicyKit.KindAllowed(cfg, AreaTier.Lot, SnapKind.Free));
                    Check.True("O7 总开关关掉 ⇒ 任何档位都不许被判定为可用", () =>
                    {
                        var off = ModConfig.CreateDefault(); off.Enabled = false;
                        foreach (AreaTier t in Enum.GetValues(typeof(AreaTier)))
                        {
                            foreach (SnapKind k in Enum.GetValues(typeof(SnapKind)))
                                if (PolicyKit.KindAllowed(off, t, k)) return false;
                        }
                        return true;
                    });
                    Check.True("O7 不带类别的 KindAllowed 只看总开关（原来那个「允许贴路缘」开关已随板块删除）", () =>
                    {
                        var on = ModConfig.CreateDefault();
                        return PolicyKit.KindAllowed(on, SnapKind.NetSide) && PolicyKit.KindAllowed(on, SnapKind.NetCentre)
                            && PolicyKit.KindAllowed(on, SnapKind.ObjectSide)
                            && !PolicyKit.KindAllowed(null, SnapKind.NetSide);
                    });

                    // 跨越规则（需求 3 留下的口径，第五轮没改）：产业区不许跨越，表面区允许但跨越段不贴合。
                    Check.Bool("O7 专门产业区不许跨越道路/建筑", true, PolicyKit.BlocksCrossing(AreaTier.Lot));
                    Check.Bool("O7 表面区域允许跨越", false, PolicyKit.BlocksCrossing(AreaTier.Surface));
                    Check.Bool("O7 市辖区不允许跨越", false, PolicyKit.BlocksCrossing(AreaTier.District));

                    // 权重：候选距离×权重取最小。路缘档在路缘类区域里是主档，不再被中心线压后（反馈 5）。
                    Check.True("O7 路缘档权重不高于中心线档（旧版那个「优先中心线」的偏向已删）", () =>
                        PolicyKit.KindWeight(cfg, SnapKind.NetSide) <= PolicyKit.KindWeight(cfg, SnapKind.NetCentre));
                    Check.True("O7 同类已有边界权重低于其它候选档（反馈 4：两个手动点都贴在同类边界上时要贴合它的节点）", () =>
                    {
                        double w = PolicyKit.KindWeight(cfg, SnapKind.AreaBorderSame);
                        foreach (SnapKind k in new[] { SnapKind.NetCentre, SnapKind.NetSide,
                            SnapKind.ObjectSide, SnapKind.AreaBorderOther, SnapKind.MapTileBorder })
                            if (!(w <= PolicyKit.KindWeight(cfg, k))) return false;
                        return true;
                    });
                    Check.True("O7 交叉口与中心线同级（旧的「优先交叉口」开关删了，但路口仍值得贴）", () =>
                        PolicyKit.KindWeight(cfg, SnapKind.NetNode) <= PolicyKit.KindWeight(cfg, SnapKind.NetCentre));
                    Check.True("O7 异类边界比同类边界吃亏（市辖区不该吸到产业区边界上）", () =>
                        PolicyKit.KindWeight(cfg, SnapKind.AreaBorderOther) > PolicyKit.KindWeight(cfg, SnapKind.AreaBorderSame));
                    Check.True("O7 所有档位的权重都是正数且有限（权重≤0 会让评分失去意义）", () =>
                    {
                        foreach (SnapKind k in Enum.GetValues(typeof(SnapKind)))
                        {
                            double w = PolicyKit.KindWeight(cfg, k);
                            if (!(w > 0) || double.IsInfinity(w) || double.IsNaN(w)) return false;
                        }
                        return true;
                    });
                    Check.True("O7 自由点权重高于一切实际档位（它只该在别的都够不着时成为结果）", () =>
                    {
                        double w = PolicyKit.KindWeight(cfg, SnapKind.Free);
                        foreach (SnapKind k in Enum.GetValues(typeof(SnapKind)))
                        {
                            if (k == SnapKind.Free) continue;
                            if (!(w >= PolicyKit.KindWeight(cfg, k))) return false;
                        }
                        return true;
                    });
                });

                // —————————————————————————————— 目标白名单（第六轮反馈 9）
                Harness.Section("PolicyKit 目标白名单：只认道路/轨道、只认建筑，埠头与内部路一律排除（反馈 9）（O8）", () =>
                {
                    // 玩家原话：「市辖区…只限制在道路/轨道中心点，不包括步行路、埠头、建筑和道路边缘、
                    // 建筑内部的道路及其它物体（如摆件、高架路墩、车辆行人等）。表面区域…只限制在道路/轨道边缘
                    // （即两侧人行道边缘，不是路缘）和建筑边缘」。
                    // 三档共用的排除名单 ⇒ 断言对 District / Lot / Surface 各跑一遍。
                    AreaTier[] tiers = { AreaTier.District, AreaTier.Lot, AreaTier.Surface };

                    Check.True("O8 道路与轨道是三档共同的目标", () =>
                    {
                        var road = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4));
                        var rail = Fix.Edge(1, NetKind.Rail, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4));
                        foreach (AreaTier t in tiers)
                            if (!PolicyKit.NetAllowed(t, road) || !PolicyKit.NetAllowed(t, rail)) return false;
                        return true;
                    });
                    Check.True("O8 高架路仍然算路（第五轮反馈 4 明确要求高架也能贴，白名单不许把它顺手排除）", () =>
                    {
                        var el = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4));
                        el.Elevated = true;
                        el.Lift = 8;
                        foreach (AreaTier t in tiers) if (!PolicyKit.NetAllowed(t, el)) return false;
                        return true;
                    });
                    Check.True("O8 步行路 / 水道 / 分类不明的 net 一律不是目标", () =>
                    {
                        var line = Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4);
                        var path = Fix.Edge(0, NetKind.Path, 0, 1, line);
                        var water = Fix.Edge(1, NetKind.Waterway, 0, 1, line);
                        var other = Fix.Edge(2, NetKind.Other, 0, 1, line);
                        foreach (AreaTier t in tiers)
                        {
                            if (PolicyKit.NetAllowed(t, path)) return false;
                            if (PolicyKit.NetAllowed(t, water)) return false;
                            if (PolicyKit.NetAllowed(t, other)) return false;
                        }
                        return true;
                    });
                    Check.True("O8 埠头（OnWater）与建筑内部路（Internal）即使挂着 Road 标记也排除", () =>
                    {
                        var line = Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4);
                        var pier = Fix.Edge(0, NetKind.Road, 0, 1, line); pier.OnWater = true;
                        var yard = Fix.Edge(1, NetKind.Road, 0, 1, line); yard.Internal = true;
                        foreach (AreaTier t in tiers)
                        {
                            if (PolicyKit.NetAllowed(t, pier)) return false;
                            if (PolicyKit.NetAllowed(t, yard)) return false;
                        }
                        return true;
                    });
                    Check.True("O8 null 边不是目标（判据必须能挡住脏数据，而不是抛异常）", () =>
                        !PolicyKit.NetAllowed(AreaTier.District, null));

                    // 地下物体：**吸附与描边都不认它**，三类区域一律（第八轮反馈 3）。
                    // 老口径是"市辖区可以贴隧道中轴"（第五轮），这一轮玩家明确撤了：
                    // "所有区域工具的节点吸附和自动贴合都不包括建在地下的所有物体"。
                    // 判据是 GraphEdge.Underground = Tunnel || 最低点高程 < -0.5（下沉路/明挖段也是"地下"）。
                    Check.True("O8 隧道：三档都既不能吸、也不能描（第八轮反馈 3）", () =>
                    {
                        var tun = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4));
                        tun.Tunnel = true;
                        foreach (AreaTier t in tiers)
                        {
                            if (PolicyKit.NetAllowed(t, tun)) return false;
                            if (PolicyKit.TraceEdgeAllowed(t, tun)) return false;
                        }
                        return true;
                    });
                    Check.True("O8 下沉路（最低点高程 -3 米）同样算地下物体", () =>
                    {
                        var sunken = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4));
                        sunken.LowElevation = -3.0;
                        foreach (AreaTier t in tiers)
                        {
                            if (PolicyKit.NetAllowed(t, sunken)) return false;
                            if (PolicyKit.TraceEdgeAllowed(t, sunken)) return false;
                        }
                        return true;
                    });
                    Check.True("O8 高架不算地下：第五轮反馈 4 要求高架照样能贴", () =>
                    {
                        var br = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(50, 0), 4));
                        br.Elevated = true;
                        br.LowElevation = 12.0;         // 整条高架都在 12 米上
                        foreach (AreaTier t in tiers)
                        {
                            if (!PolicyKit.NetAllowed(t, br)) return false;
                            if (!PolicyKit.TraceEdgeAllowed(t, br)) return false;
                        }
                        return true;
                    });
                    Check.True("O8 地下建筑不是贴合目标（反馈 3 里的「建在地下的所有物体」）", () =>
                    {
                        var line = Fix.Poly(0, 0, 10, 0, 10, 10, 0, 10, 0, 0);
                        var up = new ObjectRef { Id = 0, IsBuilding = true, Line = line };
                        var down = new ObjectRef { Id = 1, IsBuilding = true, Line = line, Underground = true };
                        foreach (AreaTier t in tiers)
                        {
                            if (!PolicyKit.ObjectAllowed(t, up)) return false;
                            if (PolicyKit.ObjectAllowed(t, down)) return false;
                        }
                        return true;
                    });

                    Check.True("O8 建筑轮廓档只认建筑：摆件/树/桥墩不放行，null 也不放行", () =>
                    {
                        var b = new ObjectRef { Id = 0, IsBuilding = true, Line = Fix.Poly(0, 0, 10, 0, 10, 10, 0, 10, 0, 0) };
                        var p = new ObjectRef { Id = 1, IsBuilding = false, Line = b.Line };
                        foreach (AreaTier t in tiers)
                        {
                            if (!PolicyKit.ObjectAllowed(t, b)) return false;
                            if (PolicyKit.ObjectAllowed(t, p)) return false;
                        }
                        return !PolicyKit.ObjectAllowed(AreaTier.Lot, null);
                    });

                    // 交叉口候选：只有步道交汇的那个点不是目标（反馈 1「空地上莫名其妙吸到鼠标旁边的节点」）。
                    var w = new WorldSnapshot();
                    w.Nodes.Add(Fix.Node(0, 0, 0));
                    w.Nodes.Add(Fix.Node(1, 100, 0));
                    w.Nodes.Add(Fix.Node(2, 100, 40));
                    w.Nodes.Add(Fix.Node(3, 200, 0));
                    w.Edges.Add(Fix.Edge(0, NetKind.Path, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 4)));
                    w.Edges.Add(Fix.Edge(1, NetKind.Road, 1, 2, Fix.Straight(Fix.XY(100, 0), Fix.XY(100, 40), 4)));
                    w.Edges.Add(Fix.Edge(2, NetKind.Road, 1, 3, Fix.Straight(Fix.XY(100, 0), Fix.XY(200, 0), 4)));
                    w.BuildAdjacency();
                    Check.Bool("O8 只有步道经过的端点不是交叉口候选", false, PolicyKit.NodeAllowed(AreaTier.District, w, 0));
                    Check.Bool("O8 有道路经过的交叉口是候选", true, PolicyKit.NodeAllowed(AreaTier.District, w, 1));
                    Check.Int("O8 挑来当描边锚点的那条边必须是道路（不能是 0 号那条步道）", 1,
                        PolicyKit.AllowedEdgeOf(AreaTier.District, w, 1));
                    Check.Int("O8 一个合法边都没有 ⇒ 交回 -1，调用方跳过这个候选", -1,
                        PolicyKit.AllowedEdgeOf(AreaTier.District, w, 0));

                    // 端到端：鼠标点在一条**人行步道**旁边，三档都不许吸上去。
                    var cfg = ModConfig.CreateDefault();
                    var pw = new WorldSnapshot();
                    pw.Nodes.Add(Fix.Node(0, 0, 0));
                    pw.Nodes.Add(Fix.Node(1, 100, 0));
                    var pe = Fix.Edge(0, NetKind.Path, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 4));
                    pe.LeftLine = Fix.Straight(Fix.XY(0, 2), Fix.XY(100, 2), 4);
                    pe.RightLine = Fix.Straight(Fix.XY(0, -2), Fix.XY(100, -2), 4);
                    pe.RoadWidth = 4;
                    pw.Edges.Add(pe);
                    pw.BuildAdjacency();
                    var click = Fix.XY(50, 0.3);
                    foreach (AreaTier t in tiers)
                    {
                        var tun2 = PolicyKit.Resolve(cfg, t, 20);
                        var got = SnapKit.FindSnap(click, pw, cfg, tun2, Fix.Free(click));
                        Check.Enum("O8 点在步道旁（" + t + "）⇒ 自由点，不吸", SnapKind.Free, got.Kind);
                    }

                    // 跨越判据与吸附目标必须共用**同一份名单**（反馈 10：规则之间不许互相打架）。
                    // 否则就会出现这种自相矛盾的形状：步道不许当贴合目标，
                    // 却能把产业区的一段边界判成「跨越了道路」而拒绝贴合 ⇒ 玩家看到的还是"不贴"。
                    Check.False("O8 横穿一条步道不算跨越（步道不在白名单里）",
                        () => SnapKit.CrossesObstacle(pw, Fix.XY(50, -6), Fix.XY(50, 6)));
                    var rw = new WorldSnapshot();
                    rw.Nodes.Add(Fix.Node(0, 0, 0));
                    rw.Nodes.Add(Fix.Node(1, 100, 0));
                    rw.Edges.Add(Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 4)));
                    rw.BuildAdjacency();
                    Check.True("O8 同一位置换成真路 ⇒ 照样算跨越（白名单没有把这条判据一起废掉）",
                        () => SnapKit.CrossesObstacle(rw, Fix.XY(50, -6), Fix.XY(50, 6)));
                    Check.False("O8 埠头横跨过去也不算跨越（它不是道路目标）", () =>
                    {
                        var pier = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XY(0, 0), Fix.XY(100, 0), 4));
                        pier.OnWater = true;
                        var pw2 = new WorldSnapshot();
                        pw2.Nodes.Add(Fix.Node(0, 0, 0));
                        pw2.Nodes.Add(Fix.Node(1, 100, 0));
                        pw2.Edges.Add(pier);
                        pw2.BuildAdjacency();
                        return SnapKit.CrossesObstacle(pw2, Fix.XY(50, -6), Fix.XY(50, 6));
                    });
                    Check.True("O8 建筑轮廓仍然挡路（反馈 9 要的是「只认建筑」，不是「不认建筑」）", () =>
                    {
                        var bw2 = new WorldSnapshot();
                        bw2.Objects.Add(new ObjectRef
                        {
                            Id = 0, OwnerIndex = 0, Signature = 7, IsBuilding = true,
                            Line = Fix.Poly(40, -10, 60, -10, 60, 10, 40, 10, 40, -10)
                        });
                        return SnapKit.CrossesObstacle(bw2, Fix.XY(50, -20), Fix.XY(50, 20));
                    });
                    Check.False("O8 摆件轮廓不挡路（树 / 桥墩不是建筑）", () =>
                    {
                        var bw3 = new WorldSnapshot();
                        bw3.Objects.Add(new ObjectRef
                        {
                            Id = 0, OwnerIndex = 0, Signature = 8, IsBuilding = false,
                            Line = Fix.Poly(40, -10, 60, -10, 60, 10, 40, 10, 40, -10)
                        });
                        return SnapKit.CrossesObstacle(bw3, Fix.XY(50, -20), Fix.XY(50, 20));
                    });
                });

                // —————————————————————————————— 叠置路网（第六轮反馈 8）
                Harness.Section("PolicyKit/SnapKit 叠置路网：中心线重合时按高度最低的那条定节点（反馈 8）（O9）", () =>
                {
                    // 代价函数本身：与高度成正比、量级极小（动不了真实的路线选择）、海平面以下夹到 0
                    //（Dijkstra 不收负权边，负权会让"已出堆即最短"这个前提失效）。
                    var low = Fix.Edge(0, NetKind.Road, 0, 1, Fix.Straight(Fix.XYH(0, 0, 0), Fix.XYH(100, 0, 0), 4));
                    var high = Fix.Edge(1, NetKind.Road, 0, 1, Fix.Straight(Fix.XYH(0, 0, 10), Fix.XYH(100, 0, 10), 4));
                    Check.True("O9 高度代价随高度单调上升（高的那条严格更贵 ⇒ 平局时它必输）",
                        () => PolicyKit.HeightTieCost(high) > PolicyKit.HeightTieCost(low));
                    Check.Close("O9 地面路（h=0）不付任何高度代价", 0, PolicyKit.HeightTieCost(low), 1e-12);
                    Check.Close("O9 10 米高的高架只贵 0.01 米（这个量级只够打破严格平局）",
                        0.01, PolicyKit.HeightTieCost(high), 1e-9);
                    Check.Close("O9 海平面以下夹到 0（不许出现负权边）", 0, PolicyKit.HeightTieCost(
                        Fix.Edge(2, NetKind.Road, 0, 1, Fix.Straight(Fix.XYH(0, 0, -8), Fix.XYH(100, 0, -8), 4))), 1e-12);
                    Check.Close("O9 拿不到几何时给 0，不给 NaN（NaN 会让整条描边的代价比较全部失效）", 0,
                        PolicyKit.HeightTieCost(null), 1e-12);

                    // 吸附：两条中心线在 XZ 上完全重合、只差高度 ⇒ 2D 距离**严格相等**
                    //（P3.DistanceTo 只看 X/Y），老口径把胜负交给四叉树的遍历顺序。
                    // 两种插入顺序都必须过 —— 只测一种的话，断言钉住的其实还是"先来者胜"。
                    var cfg = ModConfig.CreateDefault();
                    var tuning = PolicyKit.Resolve(cfg, AreaTier.District, 20);
                    var click = Fix.XY(50, 0.4);
                    for (int order = 0; order < 2; order++)
                    {
                        var w = new WorldSnapshot();
                        w.Nodes.Add(Fix.Node(0, 0, 0));
                        w.Nodes.Add(Fix.Node(1, 100, 0));
                        w.Edges.Add(order == 0 ? low : high);
                        w.Edges.Add(order == 0 ? high : low);
                        w.BuildAdjacency();
                        int lowIndex = order == 0 ? 0 : 1;
                        var r = SnapKit.FindSnap(click, w, cfg, tuning, Fix.Free(click));
                        Check.Enum("O9 叠置的两条路照样吸得上（order=" + order + "）", SnapKind.NetCentre, r.Kind);
                        Check.Int("O9 吸的是高度最低的那一条（order=" + order + "）", lowIndex, r.Edge);
                        Check.Close("O9 落点高度也是那条低路的（order=" + order + "）", 0, r.Pos.H, 1e-9);
                    }
                });
            }

            private static bool Same(ModeKnobs a, ModeKnobs b)
            {
                return Harness.Near(a.MaxDetourFactor, b.MaxDetourFactor, 1e-12)
                    && Harness.Near(a.TurnPenalty, b.TurnPenalty, 1e-12)
                    && Harness.Near(a.KindChangePenalty, b.KindChangePenalty, 1e-12)
                    && a.PreferOutermost == b.PreferOutermost
                    && a.AllowNetworkSwitch == b.AllowNetworkSwitch
                    && Harness.Near(a.CornerDegrees, b.CornerDegrees, 1e-12);
            }

            private static ModConfig Scale(AreaTier tier, double v)
            {
                var c = ModConfig.CreateDefault();
                switch (tier)
                {
                    case AreaTier.District: c.DistrictSnapDistance = v; break;
                    case AreaTier.Lot: c.LotSnapDistance = v; break;
                    case AreaTier.Surface: c.SurfaceSnapDistance = v; break;
                    default: c.MapTileSnapDistance = v; break;
                }
                return c;
            }

            private static ModConfig Mode(TraceMode m)
            {
                var c = ModConfig.CreateDefault();
                c.Mode = m;
                return c;
            }

            private static ModConfig Detail(double d)
            {
                var c = ModConfig.CreateDefault();
                c.CurveDetail = d;
                return c;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using Game;
using Game.Areas;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine.Scripting;
using ZoneSnapper.Engine;
using ZoneSnapper.GameSide;

namespace ZoneSnapper.Systems
{
    /// <summary>
    /// 提交后重写：把「最后一条边（终点 → 起点）」的描边补进真正落档的区域多边形。
    ///
    /// 【为什么必须放在这里而不是实时投影里】
    /// AreaToolSystem 提交新节点时会校验「倒数第二格 ↔ 最后一格（游标）」的距离
    /// （FACT：ATS:3509-3511，阈值 = AreaUtils.GetMinNodeDistance = m_SnapDistance*0.5）。
    /// 如果闭合边的描边点插到列表末尾，倒数第二格就变成了一个紧贴起点 m0 的自动点，
    /// 那点闭合距离必然小于阈值 ⇒ 玩家的「点回起点」这一击会被游戏**静默忽略**，区域永远闭不上。
    /// 所以闭合边只能在区域已经创建之后，从数据层补写。
    ///
    /// 【改哪些数据（全部照抄游戏自己的边界编辑路径）】
    /// 游戏自己提交区域时：ApplyAreasSystem（FACT：注册在 SystemUpdatePhase.ApplyTool，
    /// SystemOrder.cs:718）里 `SetBuffer&lt;Game.Areas.Node&gt;(...)` + `AddComponent(..., default(Updated))`
    /// （FACT：ApplyAreasSystem.cs:175/182）。也就是说 **Node 缓冲 + Updated 就是它的全部动作面**；
    /// 下游消费者（Areas.GeometrySystem 重三角化、Areas.SearchSystem 四叉树、
    /// Rendering.AreaBufferSystem 边界/填充 GPU 缓冲、Areas.UpdateCollectSystem 脏矩形、
    /// Simulation.CurrentDistrictSystem 归属重算）都只吃 Updated 这一枚脏标记。
    /// Updated 是单帧标记（FACT：CleanUpSystem 在 Cleanup 相位移除）⇒ 本系统必须跑在
    /// ApplyAreasSystem 之后、同一帧的 ApplyTool 相位里，才能看见刚建出来的 Created 区域。
    ///
    /// 【安全阀】改写前自检：环自交或退化就不写（保留游戏的粗多边形）。
    /// 这条不是洁癖：三角化一旦失败游戏会 triangles.Clear() ⇒ 区域隐形 ⇒ 四叉树为空 ⇒
    /// 连建筑都不会归属到这个区（FACT：Areas.GeometrySystem dump 893-899 的 num5=2*num4 护栏）。
    /// 宁可放弃这一次美化，也不能交付一个把玩家城市弄没的模组。
    /// </summary>
    [Preserve]
    public partial class ZoneSnapperApplySystem : GameSystemBase
    {
        private EntityQuery m_newAreasQuery;

        /// <summary>只处理本模组这一次绘制产生的环，处理完即清空。</summary>
        private List<P3> m_ring;
        private int m_skipSlave;
        private int m_applied;

        /// <summary>
        /// 连续异常计数。写区域缓冲区要走 <c>EntityManager.GetBuffer&lt;Game.Areas.Node&gt;</c>，
        /// 而这条类型索引在 v0.1.0 之前从没被模组程序集请求过（OnCreate 那次「Unknown Type」就是同一层的坑，
        /// 只是那次请求发生在建表之前）。它若真的不可用，表现会是**每帧抛一次**：
        /// 待补写的环只被 TakePendingRing 取用而不清空，catch 里把 m_ring 置空后下一帧又被取回来，
        /// 于是日志被同一条 Error 刷满 —— 玩家看不了、我们也读不了。
        /// 所以撞三次就把这条环丢弃并永久停用补写：闭合边退化成一根直线，是可以接受的；
        /// 淹掉日志会让剩下所有问题都无法定位。
        /// </summary>
        private int m_consecutiveFailures;
        private bool m_writeRouteDead;

        protected override void OnCreate()
        {
            base.OnCreate();
            // ⚠ 缓冲区在查询里要用**元素类型**写：ComponentType.ReadWrite<Game.Areas.Node>()。
            // v0.1.0 实机在这里写的是 ComponentType.ReadWrite<DynamicBuffer<Game.Areas.Node>>()，
            // 当场抛 ArgumentException: Unknown Type `DynamicBuffer`1[Game.Areas.Node]`
            // —— 根因（本轮把 Unity.Entities 反编译看完才闭环）：TypeManager 的哈希表在启动时一次性建好，
            //   **构造出来的泛型类型 DynamicBuffer<Game.Areas.Node> 没有 StableTypeHash**，只有缓冲区
            //   **元素类型**有（它是从游戏自己那句 `entityManager.AddBuffer<Game.Areas.Node>` 扫出来的）。
            //   FACT：Unity.Entities/TypeManager.cs:2092 只扫已加载程序集；TypeManagerInternal.cs:186-198
            //   对拿不到 hash 的类型直接 return 0 ⇒ GetTypeIndex 抛「Unknown Type」。
            //   游戏自己全程用元素形式：查询里 ComponentType.ReadOnly<Game.Areas.Node>()
            //   （FACT：Game.Rendering/GuideLinesSystem.cs:2651）、取用 EntityManager.GetBuffer<Game.Areas.Node>()
            //   （FACT：Game.Serialization/RequiredComponentSystem.cs:1341）、建表处 AddBuffer<Game.Areas.Node>
            //   （FACT：Game.Tools/AreaToolSystem.cs:3433）。已上线的 AccessAnarchy 也是元素形式。
            // 这个异常当时把整个 OnLoad 打断在 Harmony 之前，连带把区域工具搞坏了 —— 见 ZoneSnapperMod 第 5 步。
            m_newAreasQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Areas.Area>(),
                ComponentType.ReadOnly<Created>(),
                ComponentType.ReadWrite<Game.Areas.Node>());
        }

        protected override void OnUpdate()
        {
            if (SnapperState.Broken || m_writeRouteDead) return;

            List<P3> ring = SnapperState.TakePendingRing();
            if (ring != null) m_ring = ring;

            if (m_ring == null || m_ring.Count < 4) return;
            if (!SnapperState.Enabled) { m_ring = null; return; }

            try
            {
                // 刚建出来的区域实体。稳态下这个 query 匹配 0 个实体，成本≈0。
                if (m_newAreasQuery.IsEmptyIgnoreFilter) return;

                NativeArray<Entity> fresh = m_newAreasQuery.ToEntityArray(Allocator.TempJob);
                try
                {
                    for (int i = 0; i < fresh.Length; i++)
                    {
                        if (TryAdopt(fresh[i])) { m_applied++; m_ring = null; break; }
                    }
                }
                finally
                {
                    fresh.Dispose();
                }
                m_consecutiveFailures = 0;
            }
            catch (Exception e)
            {
                m_ring = null;
                m_consecutiveFailures++;
                if (m_consecutiveFailures < 3)
                {
                    SnapperLog.Error("[ZoneSnapper] 提交后补写闭合边失败（区域保持游戏原样）：" + e.GetType().Name + " " + e.Message);
                    return;
                }
                // 第三次：这条类型索引对模组不可用，永久停用补写并留下一行可判读的证据。
                // 主链路（绘制中的自动贴合与描边）不受影响，玩家照样能画，只是闭合那条边保持游戏的直线。
                m_writeRouteDead = true;
                SnapperState.DiscardPendingRing();
                SnapperLog.Error(string.Format(
                    "[ZoneSnapper] 连续 {0} 次无法读写 Game.Areas.Node 缓冲区（{1}）：本局停用「闭合边补写」，" +
                    "绘制中的自动贴合照常。原因与修法见开发笔记「模组侧写 ECS 缓冲区」一节。",
                    m_consecutiveFailures, e.GetType().Name + ": " + e.Message));
            }
        }

        /// <summary>
        /// 这个新区域是不是我们刚画的那个？判据：它现有的节点序列与我们的投影环逐点重合。
        /// 逐点比对而不是「时间上挨着就算」—— 同一帧可能有别的模组或别的工具建区域。
        /// </summary>
        private bool TryAdopt(Entity area)
        {
            // 有没有 Node 缓冲区由查询本身保证（m_newAreasQuery 的第三个类型就是它），
            // 这里不再用 HasComponent<Game.Areas.Node> 复检 —— 缓冲区元素类型在 1.3 的
            // HasComponent/HasBuffer 语义下不是一回事，问了反而可能问错。

            // Slave 区域的 Node 会被游戏从 Owner 覆写（FACT：Areas.GeometrySystem dump 128-136/172-190）
            // ⇒ 我们改了也白改，还会被覆写回旧形状，直接跳过。
            if (EntityManager.HasComponent<Game.Areas.SubArea>(area))
            {
                m_skipSlave++;
                return false;
            }

            DynamicBuffer<Game.Areas.Node> nodes = EntityManager.GetBuffer<Game.Areas.Node>(area);
            List<P3> current = new List<P3>(nodes.Length);
            for (int i = 0; i < nodes.Length; i++)
            {
                current.Add(new P3(nodes[i].m_Position.x, nodes[i].m_Position.z, nodes[i].m_Position.y));
            }
            if (!MatchesOurProjection(current)) return false;

            // 自检：新环不能自交、不能退化，否则不写。
            List<P3> proposed = new List<P3>(m_ring.Count);
            for (int i = 0; i < m_ring.Count; i++) proposed.Add(m_ring[i]);
            if (SimplifyKit.SelfIntersects(Close(proposed)))
            {
                SnapperLog.Info("[ZoneSnapper] 闭合边描边会产生自交边界，保持游戏原形状");
                return true;    // 认领了但不改，避免下一帧重复判定
            }
            double area2 = Math.Abs(GeoKit.SignedArea2D(proposed));
            if (area2 < 25.0)
            {
                SnapperLog.Info("[ZoneSnapper] 新环面积退化（" + area2.ToString("F1") + " m²），保持游戏原形状");
                return true;
            }

            nodes.Clear();
            for (int i = 0; i < proposed.Count; i++)
            {
                P3 p = proposed[i];
                // 游戏自己写区域节点时 elevation 传 float.MinValue（FACT：ATS:1881）
                nodes.Add(new Game.Areas.Node(new float3((float)p.X, (float)p.H, (float)p.Y), float.MinValue));
            }
            if (!EntityManager.HasComponent<Updated>(area))
            {
                EntityManager.AddComponent(area, ComponentType.ReadWrite<Updated>());
            }
            else
            {
                EntityManager.SetComponentData(area, default(Updated));
            }
            SnapperLog.Info(string.Format(
                "[ZoneSnapper] 闭合边已补写：区域节点 {0} → {1} 个（累计改写 {2} 次，跳过从属区域 {3} 次）",
                current.Count, proposed.Count, m_applied + 1, m_skipSlave));
            return true;
        }

        /// <summary>闭合显示用：末尾补回首点，方便共用折线自交判据。</summary>
        private static List<P3> Close(List<P3> ring)
        {
            List<P3> l = new List<P3>(ring.Count + 1);
            l.AddRange(ring);
            if (l.Count > 0) l.Add(l[0]);
            return l;
        }

        /// <summary>
        /// 游戏建出来的那份节点表应当正是我们写的投影（闭合点击那一帧游戏会把游标格也写进去，
        /// 所以我们比对时允许「多一个与首点重合的尾巴」）。
        /// </summary>
        private bool MatchesOurProjection(List<P3> current)
        {
            if (m_ring == null || m_ring.Count < 4) return false;
            int ourManualRing = SnapperState.PendingSlotCount;
            if (ourManualRing < 3) return false;
            if (current.Count < ourManualRing) return false;
            double eps = 0.05;
            int n = Math.Min(current.Count, ourManualRing);
            for (int i = 0; i < n; i++)
            {
                if (current[i].DistanceTo(m_ring[i]) > eps) return false;
            }
            return true;
        }

        /// <summary>切换工具/新绘制时由主系统调用，丢掉没消费完的环。</summary>
        public static void DiscardPending()
        {
            SnapperState.DiscardPendingRing();
        }
    }
}

using System;
using System.Collections.Generic;
using ZoneSnapper.Engine;

namespace ZoneSnapper.T3
{
    internal static partial class Tests
    {
        /// <summary>
        /// L 段：选项页词条与游戏的 key 模板（实机 v0.1.0/0.1.1 玩家看到满屏 key 的回归位）。
        ///
        /// 这一段的全部价值是把「界面显示的是文字还是 key」变成离线可判定的口径。
        /// 游戏查不到词条时会把 key 原样画上（FACT：Game.UI.Localization/UILocalizationManager.cs:19-29
        /// 的 else 分支 data.Set(key)），而能不能查到，取决于我们注册的 key 与游戏生成的模板是否**逐字**相同 ——
        /// 少一段前缀、差一个方括号，整页就全是代码味道的字符串。所以钉三件事：
        ///  ① 模板逐字抄反编译（FACT：Game.Modding/ModSetting.cs:303-371），期望串写死在 L1；
        ///  ② 每行、每种官方语言都有词（缺语言不是「兜底成功」，玩家看到的是中英混排）；
        ///  ③ 每个上屏项都有标题**和**说明：说明缺失时那一行下面会挂着
        ///     Options.OPTION_DESCRIPTION[...]，与标题缺失是同一个症状，只补标题不算修完。
        /// </summary>
        internal static class Locale
        {
            // 本模组的 id / name（FACT：ModSetting.cs:36-44
            //   id = 程序集名 + "." + Mod 类命名空间 + "." + Mod 类名，name = 设置类名）
            private const string ID = "ZoneSnapper.ZoneSnapper.ZoneSnapperMod";
            private const string NAME = "ZoneSnapperSetting";

            public static void Run()
            {
                Harness.Section("L1 游戏的 key 模板逐字对账（ModSetting.cs:303-371）", () =>
                {
                    Check.Str("L1 页标题", "Options.SECTION[" + ID + "]", LocaleKit.SectionKey(ID));
                    Check.Str("L1 行标题", "Options.OPTION[" + ID + "." + NAME + ".Enabled]",
                        LocaleKit.LabelKey(ID, NAME, "Enabled"));
                    Check.Str("L1 行说明", "Options.OPTION_DESCRIPTION[" + ID + "." + NAME + ".Enabled]",
                        LocaleKit.DescKey(ID, NAME, "Enabled"));
                    Check.Str("L1 标签页", "Options.TAB[" + ID + ".Snap]", LocaleKit.TabKey(ID, "Snap"));
                    Check.Str("L1 分组", "Options.GROUP[" + ID + ".MainSwitch]", LocaleKit.GroupKey(ID, "MainSwitch"));
                    Check.Str("L1 按键映射", "Options.INPUT_MAP[" + ID + "]", LocaleKit.BindingMapKey(ID));
                    // 第六轮实机事故的本体：下拉选项的 key 少了 id 那一段 ⇒ 游戏查不到 ⇒ 整列显示成 key。
                    Check.Str("L1 下拉选项 key 带 id 前缀（AutomaticSettings.cs:846-855 + :906-913）",
                        "Options." + ID + ".TRACEMODE[FewestCorners]", LocaleKit.EnumKey(ID, "TraceMode", "FewestCorners"));

                    // 事故本体：老词条表注册的是 "Enabled" 这种裸名，游戏永远不会去查它。
                    Check.Bool("L1 裸属性名不是游戏要的 key（旧实现注册的正是这种）", false,
                        "Enabled".Equals(LocaleKit.LabelKey(ID, NAME, "Enabled"), StringComparison.Ordinal));
                });

                Harness.Section("L2 官方语言列表", () =>
                {
                    Check.Int("L2 12 种官方语言", 12, LocaleKit.kLocales.Length);
                    string[] must =
                    {
                        "en-US", "zh-HANS", "zh-HANT", "de-DE", "es-ES", "fr-FR",
                        "it-IT", "ja-JP", "ko-KR", "pl-PL", "pt-BR", "ru-RU"
                    };
                    for (int i = 0; i < must.Length; i++)
                    {
                        string l = must[i];
                        Check.Bool("L2 含 " + l, true, IndexOfLocale(l) >= 0);
                    }
                    Check.Int("L2 未知语言退回英文列", 1, LocaleKit.ColumnOf("xx-XX"));
                    Check.Int("L2 locale 大小写不敏感", LocaleKit.ColumnOf("zh-HANT"), LocaleKit.ColumnOf("ZH-hant"));
                });

                Harness.Section("L3 每行每种语言都有词（缺一项就是玩家看到的中英混排）", () =>
                {
                    List<string> problems = LocaleKit.Validate();
                    for (int i = 0; i < problems.Count && i < 12; i++) Check.Bad("L3 表结构问题", problems[i]);
                    Check.Int("L3 结构自检无问题（列数 / 空格子）", 0, problems.Count);

                    int rows = 0;
                    List<string> sameHant = new List<string>();
                    foreach (string slug in LocaleKit.Slugs())
                    {
                        rows++;
                        if (!HantDiffersFromHans(slug)) sameHant.Add(slug);
                    }
                    // 行数账：第六轮重排后 = 1 模组名 + 3 页名 + 11 分组 + 20 项标题 + 17 项说明
                    //                              + 1 按键映射页 + 5 个下拉选项 = 58；
                    //          第六轮之后追加「重置所有设置项」那颗按钮：+1 分组（KeysReset）+1 标题
                    //          +1 说明 +1 确认弹窗文本 = 62；
                    //          第八轮反馈 1 追加「关于」那块：+1 分组（About）+2 只读文本标题 +2 说明
                    //          +3 跳转按钮标题 +3 按钮说明 = 73。
                    // （第五轮是 61：三个区域类型开关的说明按第六轮反馈 4 删了。）
                    // 名单里每项都要求标题+说明齐全（豁免见 L4），所以这个数只是「表没有整体缩水」的下界哨兵，
                    // 真正的账由 L4 逐条对。
                    Check.True("L3 词条行数足够覆盖选项页（标题 + 说明 + 分组 + 页名 + 确认文本 ≥ 73）", () => rows >= 73);
                    // 繁简照抄＝v0.1.0 的内容缺口（那时 zh-HANT 直接复用简体）。
                    // 少数词繁简本来就同形（如「弧度」），必须**显式**列在下面 ——
                    // 新增一条同形行就得来这里签一次名，避免「整列忘了转换」被悄悄放过。
                    for (int i = 0; i < sameHant.Count; i++)
                    {
                        if (kHantSameByDesign.IndexOf(sameHant[i]) < 0)
                        {
                            Check.Bad("L3 zh-HANT 照抄了 zh-HANS", sameHant[i]);
                        }
                    }
                    Check.Int("L3 意外繁简同形的行数", 0, CountUnexpectedSameHant(sameHant));
                    // 名单本身也不能过期：列在这里的行必须确实还同形，否则该从名单里删掉
                    for (int i = 0; i < kHantSameByDesign.Count; i++)
                    {
                        string s = kHantSameByDesign[i];
                        Check.Bool("L3 同形名单里的 " + s + " 仍然存在且同形", true,
                            LocaleKit.Has(s) && !HantDiffersFromHans(s));
                    }
                });

                Harness.Section("L4 每个上屏设置项都有标题与说明", () =>
                {
                    int labelOnly = 0;
                    for (int i = 0; i < LocaleKit.kRows.Length; i++)
                    {
                        string row = LocaleKit.kRows[i];
                        Check.Bool("L4 " + row + " 有标题", true, LocaleKit.Has(row));
                        // 两类「只有标题、没有说明」是**有意**的：
                        //  ① 下拉框的**选项**：游戏给 EnumDropdown 每一项查的是 Options.<id>.TRACEMODE[Smart] 这种 key
                        //     （FACT：AutomaticSettings.cs:846-855 + :906-913），面板上根本没有「这一项的说明」那一格；
                        //  ② 三个区域类型开关（第六轮反馈 4）：玩家在游戏工具栏上已经认识这三个工具，说明文本是噪音。
                        if (row.StartsWith("mode.", StringComparison.Ordinal) || kLabelOnlyRows.IndexOf(row) >= 0)
                        { labelOnly++; continue; }
                        Check.Bool("L4 " + row + " 有说明", true, LocaleKit.Has(row + ".desc"));
                    }
                    Check.Int("L4 只要求标题的行 = 5 个下拉选项 + 3 个区域开关", 8, labelOnly);
                    // 名单长度：v0.1.4 是 28 项；第五轮反馈 2/5/6/7 重排之后是 25 项
                    //（删：3 个优先级开关、5 个描边与节点设置、太空区域、统一倍率 + 行政区额外倍率、
                    //  自由边弧化 3 项、绘制中实时跟随、显示标记 = 净 -11；
                    //  增：3 条分类吸附距离、贴合模式 + 5 个选项、节点续接、跟随高亮、退回跟随快捷键 = +11，
                    //  另 4 项改名不换行 ⇒ 28 - 11 + 8 = 25）。
                    // 数字变了必须来这里签一次名：这一行就是「选项页上有哪些行」的账。
                    // 第六轮追加「重置所有设置项」那颗按钮（+1）⇒ 25 → 26；
                    // 第八轮反馈 1 追加「关于」那块（+2 只读文本 +3 跳转按钮）⇒ 26 → 31。
                    // 这些行全都上屏 ⇒ 必须进名单：漏了的话游戏侧反射会打一行 Warn，
                    // 而实机日志是我们唯一的眼睛。
                    Check.Int("L4 设置项名单 31 项", 31, LocaleKit.kRows.Length);
                    Check.True("L4 名单里不许有重复项", () =>
                    {
                        var seen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string r in LocaleKit.kRows) if (!seen.Add(r)) return false;
                        return true;
                    });
                    Check.True("L4 每个 mode.* 选项都能按游戏的枚举模板取到 key（含 id 前缀）", () =>
                    {
                        string k = LocaleKit.EnumKey(ID, "TraceMode", "FewestCorners");
                        return k == "Options." + ID + ".TRACEMODE[FewestCorners]" && LocaleKit.Has("mode.FewestCorners");
                    });

                    // 第六轮反馈 5：这一档的语义从「路网变了就自动重描存档」改成「按键才写盘」。
                    // 词条必须跟着改口径 —— 标题还写着「自动跟随（实验性）」就是在承诺一个已经删掉的行为，
                    // 玩家照着标题去等自动跟随，等不到就又变成"不太靠谱"。
                    Check.True("L4 FollowCommitted 标题改成「由那颗键触发」，不再自称自动跟随", () =>
                    {
                        string label = LocaleKit.Cell("FollowCommitted", "zh-HANS");
                        return label.IndexOf("重算贴合区域") >= 0 && label.IndexOf("自动跟随") < 0;
                    });
                    Check.True("L4 FollowCommitted 标题在 12 个语种里都不再挂「实验性」这块牌子", () =>
                    {
                        for (int c = 0; c < LocaleKit.kLocales.Length; c++)
                        {
                            string s = LocaleKit.RawCell("FollowCommitted", c);
                            if (s.IndexOf("experimental", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                            if (s.IndexOf("实验性", StringComparison.Ordinal) >= 0) return false;
                            if (s.IndexOf("實驗性", StringComparison.Ordinal) >= 0) return false;
                            if (s.IndexOf("experimentell", StringComparison.OrdinalIgnoreCase) >= 0) return false;
                        }
                        return true;
                    });
                    Check.True("L4 FollowCommitted 说明写清三件事：自己不改存档 / 按键才写 / 能退回", () =>
                    {
                        string d = LocaleKit.Cell("FollowCommitted.desc", "zh-HANS");
                        return d.IndexOf("绝不改存档") >= 0 && d.IndexOf("重算贴合区域") >= 0 && d.IndexOf("退回") >= 0;
                    });

                    // 测试前追加的那颗「重置所有设置项」按钮（用户 2026-09-24 要求）。
                    // 它是本模组**唯一**一个会一次性抹掉玩家全部自定义的入口 ⇒ 文案必须把代价写在按钮上，
                    // 而不是等按下去才说：说明里点名「四颗快捷键一起清空」，因为玩家最容易忽略的就是这一条。
                    Check.True("L4 ResetAllSettings 标题 12 个语种全有（缺一种就是那一语种看到兜底英文/键名）", () =>
                    {
                        for (int c = 0; c < LocaleKit.kLocales.Length; c++)
                        {
                            if (string.IsNullOrEmpty(LocaleKit.RawCell("ResetAllSettings", c))) return false;
                        }
                        return true;
                    });
                    Check.True("L4 ResetAllSettings 说明点名的两件事：快捷键会清空 / 无法撤销", () =>
                    {
                        string d = LocaleKit.Cell("ResetAllSettings.desc", "zh-HANS");
                        return d.IndexOf("快捷键") >= 0 && d.IndexOf("无法撤销") >= 0;
                    });
                    // 确认弹窗走的是 Options.WARNING[<属性全名>] 这条**独立**模板
                    //（FACT：AutomaticSettings.cs:1146-1158 带 confirmMessageId 时查 "Options.WARNING[" + id + "]"；
                    //  + :1051-1060 的 [SettingsUIConfirmation] ⇒ WidgetType.BoolButtonWithConfirmation），
                    // 与 OPTION / OPTION_DESCRIPTION 不是一个 key ⇒ 注册漏一段就是弹窗永远显示兜底文本。
                    Check.Str("L4 确认弹窗 key = Options.WARNING[属性全名]（逐字，勿改模板）",
                        "Options.WARNING[" + ID + "." + NAME + ".ResetAllSettings]",
                        LocaleKit.WarningKey(ID, NAME, "ResetAllSettings"));
                    Check.Bool("L4 reset.confirm 有 12 语言（弹窗漏一种语言=按下去看到一串代码）", true,
                        LocaleKit.Has("reset.confirm"));
                    // 弹窗文本自己也得说清代价：这颗按钮会连快捷键一起清掉
                    Check.True("L4 reset.confirm 正文点了「快捷键」和「无法撤销」", () =>
                    {
                        string c = LocaleKit.Cell("reset.confirm", "zh-HANS");
                        return c.IndexOf("快捷键") >= 0 && c.IndexOf("无法撤销") >= 0;
                    });
                    // 「重置」这一格分组名繁简只差一个字形（重置 / 重設）：它**不在** kHantSameByDesign 里，
                    // 所以哪天简体被照抄到繁体列，L3 那条同形探针就报红。这里再正面钉一次它确实不同形。
                    Check.True("L4 group.KeysReset 繁简不同形（不是照抄简体）", () =>
                    {
                        string h = LocaleKit.Cell("group.KeysReset", "zh-HANS");
                        string t = LocaleKit.Cell("group.KeysReset", "zh-HANT");
                        return !string.IsNullOrEmpty(h) && !string.IsNullOrEmpty(t) && !string.Equals(h, t, StringComparison.Ordinal);
                    });

                    // ——— 第八轮反馈 1：「关于」那块（版本 / 作者 / 三颗跳转按钮）
                    Check.True("L4 关于那五行全都上屏且 12 语言齐全", () =>
                    {
                        string[] about = { "ModVersionText", "AuthorNameText", "OpenKoFiLink", "OpenForumLink", "OpenRainbowLink" };
                        for (int i = 0; i < about.Length; i++)
                        {
                            if (!LocaleKit.IsKnownRow(about[i])) return false;
                            if (!LocaleKit.Has(about[i]) || !LocaleKit.Has(about[i] + ".desc")) return false;
                            for (int c = 0; c < LocaleKit.kLocales.Length; c++)
                            {
                                if (string.IsNullOrEmpty(LocaleKit.RawCell(about[i], c))) return false;
                                if (string.IsNullOrEmpty(LocaleKit.RawCell(about[i] + ".desc", c))) return false;
                            }
                        }
                        return true;
                    });
                    // 这一条是**诚实性**守卫，跟 L6 那两条同族：本模组还没发布，那顆「论坛页面」按钮指向的是
                    // 同作者系列的 Access Anarchy 帖子。文案必须把这句话说出来，不能让按钮冒充"本模组的论坛页"。
                    // 哪天真的发布了、链接换成自己的帖子，来这里把这条改掉（同时改 OpenForumLink.desc）。
                    Check.True("L4 论坛那颗按钮的说明写清了「还没发布，现在指向的是别的帖子」", () =>
                    {
                        string d = LocaleKit.Cell("OpenForumLink.desc", "zh-HANS");
                        return d.IndexOf("还没发布") >= 0 && d.IndexOf("Access Anarchy") >= 0;
                    });
                    Check.True("L4 三颗跳转按钮的说明都点明「会打开浏览器」", () =>
                    {
                        string[] keys = { "OpenKoFiLink.desc", "OpenForumLink.desc", "OpenRainbowLink.desc" };
                        for (int i = 0; i < keys.Length; i++)
                        {
                            string d = LocaleKit.Cell(keys[i], "zh-HANS");
                            if (d.IndexOf("浏览器") < 0) return false;
                        }
                        return true;
                    });
                });

                Harness.Section("L5 取词行为：绝不返回 key 本身", () =>
                {
                    Check.Bool("L5 已知 slug 取到非空文案", true,
                        !string.IsNullOrEmpty(LocaleKit.Text("Enabled", "de-DE")));
                    Check.Str("L5 未知语言退回英文而不是返回 null",
                        LocaleKit.Text("Enabled", "en-US"), LocaleKit.Text("Enabled", "nl-NL"));
                    Check.Null("L5 没有这一行时返回 null（游戏侧据此记 MissingSlugs）",
                        LocaleKit.Text("NoSuchRow", "en-US"));
                    Check.Bool("L5 文案里不含 Options. / CO. 前缀（防把 key 抄成文案）", true, NoValueLooksLikeKey());
                    Check.Bool("L5 null / 空 slug 不抛也不说有", false, LocaleKit.Has(null) || LocaleKit.Has(""));
                });

                Harness.Section("L6 官方用词对账（第五轮反馈 9；期望串来自 scripts/extract-official-terms.py 的实测抽取）", () =>
                {
                    // 【这一段的口径】只钉**取得到证**的词。证据文件：research/locale-official-terms.md，
                    // 由 scripts/extract-official-terms.py 从 `Cities2_Data/Content/Game/Locale.cok`（普通 ZIP，
                    // 成员 <lang>.loc）现抽现生成，每语言 11026 条 key。要改下面任何一格词，先跑那个脚本看表。
                    //
                    // 【反面教训，别删这段注释】上一版这里有 6 条"Snapping 页名的官方词"断言
                    // （zh-HANS 对齐 / ru Привязка / fr Accrocher / pt Aderir / es Ajustar / zh-HANT 吸附），
                    // 出处写的是 `Toolbar.SNAPPING_TITLE`。本轮实测：**Snapping 与吸附/对齐这组词在 12 种语言的
                    // key 和值里都命中 0 次**，那个 key 在本机安装里不存在（Blob.cok 里也扫不到明文）⇒
                    // 那六条是"我们抄了一份来路不明的表"，全部撤掉，页名回到玩家词（见下面 tab.snap 那两条）。
                    // 同理撤掉的还有两条 .desc 里"游戏里这一档叫「快速对齐…」"的引用（下面有守卫断言）。
                    Check.Str("L6 市辖区 ja 用游戏的「特区」（不是地区）", "特区", LocaleKit.Text("SnapDistrict", "ja-JP"));
                    Check.Str("L6 市辖区 zh-HANT 用游戏的「行政區」", "行政區", LocaleKit.Text("SnapDistrict", "zh-HANT"));
                    Check.Str("L6 市辖区 it 用游戏的「Quartieri」", "Quartieri", LocaleKit.Text("SnapDistrict", "it-IT"));
                    Check.Str("L6 市辖区 ru 用游戏的「Районы」", "Районы", LocaleKit.Text("SnapDistrict", "ru-RU"));
                    Check.Str("L6 市辖区 pl 用游戏的「Dzielnice」", "Dzielnice", LocaleKit.Text("SnapDistrict", "pl-PL"));
                    Check.Str("L6 产业区 en 用游戏的拼写（z，不是行业惯用的 s）",
                        "Specialized Industry Area", LocaleKit.Text("SnapLot", "en-US"));
                    // 上一版这条抄成了「産業特化エリア」——词序反了，游戏自己写的是 特化産業エリア。
                    Check.Str("L6 产业区 ja 用游戏的「特化産業エリア」", "特化産業エリア", LocaleKit.Text("SnapLot", "ja-JP"));
                    Check.Str("L6 产业区 zh-HANT 用游戏的「特殊工業功能區域」",
                        "特殊工業功能區域", LocaleKit.Text("SnapLot", "zh-HANT"));
                    Check.Str("L6 产业区 ko 用游戏的「특화 산업 구역」", "특화 산업 구역", LocaleKit.Text("SnapLot", "ko-KR"));
                    Check.Str("L6 表面区域 ja 用游戏的「地表エリア」", "地表エリア", LocaleKit.Text("SnapSurface", "ja-JP"));
                    Check.Str("L6 表面区域 ru 用游戏的「Поверхности」", "Поверхности", LocaleKit.Text("SnapSurface", "ru-RU"));
                    Check.Str("L6 表面区域 ko 用游戏的「표면적」", "표면적", LocaleKit.Text("SnapSurface", "ko-KR"));
                    Check.Str("L6 表面区域 zh-HANT 用游戏的「地面區域」", "地面區域", LocaleKit.Text("SnapSurface", "zh-HANT"));

                    // 城市边界线那一格是"玩家词 + 官方名"两段式：官方名必须 12 种语言一个都不少，
                    // 否则玩家在自己的语言里对不出这是游戏里的哪个工具（断言的是游戏原词，见证据文件那行 Map Tiles）。
                    string[,] kMapTileOfficial =
                    {
                        { "en-US", "Map Tiles" }, { "de-DE", "Kartenfelder" }, { "fr-FR", "Carreaux de carte" },
                        { "es-ES", "Casillas de mapa" }, { "it-IT", "Quadranti della mappa" }, { "pl-PL", "Pola mapy" },
                        { "pt-BR", "Quadrados do mapa" }, { "ru-RU", "Клетки карты" }, { "ja-JP", "マップタイル" },
                        { "ko-KR", "지도 타일" }, { "zh-HANS", "地图区块" }, { "zh-HANT", "地圖區塊" },
                    };
                    for (int i = 0; i < kMapTileOfficial.GetLength(0); i++)
                    {
                        string lang = kMapTileOfficial[i, 0];
                        string official = kMapTileOfficial[i, 1];
                        string cell = LocaleKit.Text("SnapMapTile", lang);
                        Check.True("L6 城市边界线 " + lang + " 里带着游戏的「" + official + "」", () =>
                            cell != null && cell.Contains(official));
                    }

                    // 两处玩家点名的词优先于官方词（反馈 6：地图瓦片→城市边界线；反馈 3：贴合范围→节点吸附距离）。
                    Check.True("L6 城市边界线：玩家要的中文名在，游戏的官方名也在括号里", () =>
                    {
                        string s = LocaleKit.Text("SnapMapTile", "zh-HANS");
                        return s.Contains("城市边界线") && s.Contains("地图区块");
                    });
                    Check.True("L6 城市边界线：英文格同理（City Border + Map Tiles）", () =>
                    {
                        string s = LocaleKit.Text("SnapMapTile", "en-US");
                        return s.Contains("City Border") && s.Contains("Map Tiles");
                    });
                    Check.True("L6 四条距离滑杆都带「节点吸附距离」这个玩家词（不改成官方动词）", () =>
                    {
                        string[] slugs = { "DistrictSnapPercent", "LotSnapPercent", "SurfaceSnapPercent", "MapTileSnapPercent" };
                        foreach (string s in slugs)
                            if (!LocaleKit.Text(s, "zh-HANS").Contains("节点吸附距离")) return false;
                        return true;
                    });

                    // 守卫一：取不到证的"官方词"不许再写进文案（这两串正是上一版从不存在的那张表里抄来的）。
                    Check.True("L6 文案里不再出现没取证到的「快速对齐 / 快速對齊」这一档游戏选项名", () =>
                    {
                        foreach (string s in LocaleKit.Slugs())
                        {
                            for (int c = 1; c <= LocaleKit.kLocales.Length; c++)
                            {
                                string v = LocaleKit.RawCell(s, c);
                                if (v != null && (v.Contains("快速对齐") || v.Contains("快速對齊"))) return false;
                            }
                        }
                        return true;
                    });
                    // 守卫二：页名与本模组正文用词自洽（我们既然证不到游戏的 Snapping 译法，就必须全程只用玩家词）。
                    Check.True("L6 页名与「节点吸附距离」用的是同一个「吸附」，繁简一致", () =>
                    {
                        string tab = LocaleKit.Text("tab.snap", "zh-HANS");
                        return tab == "吸附" && LocaleKit.Text("tab.snap", "zh-HANT") == "吸附"
                            && LocaleKit.Text("group.SnapDistance", "zh-HANS").Contains("吸附");
                    });
                    Check.True("L6 语言列表里没有 es-MX（这份安装根本没有这一列，注册了也只是空跑）", () =>
                    {
                        foreach (string l in LocaleKit.kLocales)
                            if (string.Equals(l, "es-MX", System.StringComparison.OrdinalIgnoreCase)) return false;
                        return true;
                    });
                });
            }

            /// <summary>
            /// 繁简**本来就同形**的行（显式签名，不接受「忘了转换」混进来）。
            /// 判定见 HantDiffersFromHans：含汉字且两列一样 ⇒ 要么在这里签名，要么去改表。
            ///
            /// 现在只有一条，而且理由是"取不到证"而不是"本来就同形"：
            ///   tab.snap —— 上一版这里签的是"官方简体叫对齐、繁体叫吸附"，本轮实测那个 key 在本机 Locale.cok 里
            ///   根本不存在（Snapping 一词 12 种语言 0 命中）⇒ 我们没有任何依据让繁简分开，两列统一用玩家词「吸附」，
            ///   与本表其它 15 处「吸附」一致。哪天取到证了（比如从压缩资源里解出 UI 字符串表），来这里改一次。
            /// 除此之外**任何**新增的同形行都说明某一列被照抄了 —— 保持这张名单短就是这条探针的全部价值。
            /// </summary>
            private static readonly List<string> kHantSameByDesign = new List<string>
            {
                "tab.snap",
                // 「作者」这两个字繁简同形（不是照抄简体 —— 简体没有更简单的写法可写了）。
                // 第八轮反馈 1 加的「关于 → 作者」那一行；它的**说明**行仍是两套写法（模组 / 模組），
                // 所以这块整体没有偷懒。
                "AuthorNameText",
            };

            /// <summary>
            /// 只有标题、**故意**没有说明的设置项（第六轮反馈 4：三个区域类型开关不要说明文本）。
            /// 想给它们加回说明？来这里把名字删掉，L4 就会开始要求 .desc。
            /// </summary>
            private static readonly List<string> kLabelOnlyRows = new List<string>
            {
                "SnapDistrict", "SnapLot", "SnapSurface",
            };

            private static int CountUnexpectedSameHant(List<string> sameHant)
            {
                int n = 0;
                for (int i = 0; i < sameHant.Count; i++)
                {
                    if (kHantSameByDesign.IndexOf(sameHant[i]) < 0) n++;
                }
                return n;
            }

            private static bool NoValueLooksLikeKey()
            {
                foreach (string s in LocaleKit.Slugs())
                {
                    for (int c = 1; c <= LocaleKit.kLocales.Length; c++)
                    {
                        string v = LocaleKit.RawCell(s, c);
                        if (v != null && (v.StartsWith("Options.", StringComparison.Ordinal)
                                          || v.StartsWith("CO.", StringComparison.Ordinal))) return false;
                    }
                }
                return true;
            }

            private static bool HantDiffersFromHans(string slug)
            {
                string s = LocaleKit.RawCell(slug, LocaleKit.ColumnOf("zh-HANS"));
                string t = LocaleKit.RawCell(slug, LocaleKit.ColumnOf("zh-HANT"));
                if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(t)) return true;   // 缺失由 Validate 报
                if (!HasHan(s)) return true;                    // 没有汉字，本来就该一样
                return s != t;
            }

            private static bool HasHan(string s)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    int c = s[i];
                    if (c >= 0x4E00 && c <= 0x9FFF) return true;
                }
                return false;
            }

            private static int IndexOfLocale(string l)
            {
                for (int i = 0; i < LocaleKit.kLocales.Length; i++)
                {
                    if (string.Equals(LocaleKit.kLocales[i], l, StringComparison.Ordinal)) return i;
                }
                return -1;
            }
        }
    }
}

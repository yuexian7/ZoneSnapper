using System;
using System.Collections.Generic;

namespace ZoneSnapper.Engine
{
    /// <summary>
    /// 选项页词条表与**游戏的 key 模板**。纯 BCL，回归壳直接编进来断言（L 段）。
    ///
    /// 【为什么这一层存在】实机 v0.1.0/0.1.1 玩家看到的是满屏的 key 名而不是文字。
    /// 根因不是「翻译没写」，而是**我们注册的 key 根本不是游戏查询的那个 key**：
    /// 游戏建选项行时用的是 <c>Options.OPTION[&lt;id&gt;.&lt;settingClass&gt;.&lt;Property&gt;]</c> 这种带方括号的模板，
    /// 而我们词条表里写的是 <c>"Enabled"</c>、<c>"EnabledDesc"</c> 这种裸名 ⇒ 一条都命中不了 ⇒
    /// 查不到就原样把 key 画上去（FACT：Game.UI.Menu/AutomaticSettings.cs:327-368 的 GetPath/GetDisplayName/GetDescription，
    /// 查不到时 LocalizedString.Id(key) 直接把 key 当值显示）。
    /// 同类事故在前两个模组里都出现过，口径是：**模板逐字抄反编译，绝不自己发明；
    /// 并且让回归壳把「模板」与「每种语言每行都有词」钉死。**
    ///
    /// 【模板出处（FACT，逐字核对过）】Game.Modding/ModSetting.cs:
    ///   :303 GetSettingsLocaleID   → "Options.SECTION[" + id + "]"
    ///   :308 GetOptionLabelLocaleID → "Options.OPTION[" + id + "." + name + "." + optionName + "]"
    ///   :313 GetOptionDescLocaleID  → "Options.OPTION_DESCRIPTION[" + id + "." + name + "." + optionName + "]"
    ///   :323 GetOptionTabLocaleID   → "Options.TAB[" + id + "." + tabName + "]"
    ///   :328 GetOptionGroupLocaleID → "Options.GROUP[" + id + "." + groupName + "]"
    ///   :368 GetBindingMapLocaleID  → "Options.INPUT_MAP[" + id + "]"
    /// 其中 id = 程序集名 + "." + Mod 类的命名空间 + "." + Mod 类名，name = 设置类名
    /// （FACT：ModSetting.cs:36-44）。本模组 ⇒ "ZoneSnapper.ZoneSnapper.ZoneSnapperMod" 与 "ZoneSnapperSetting"。
    /// 游戏侧一律**调用 ModSetting 自己的方法**取 key，本文件的模板只用于离线断言与日志核对。
    /// </summary>
    public static class LocaleKit
    {
        /// <summary>
        /// 官方支持的语言（FACT：Game.Settings/InterfaceSettings.cs:266 的 GetSupportedLocales()，
        /// 与 AccessAnarchy / BridgeTheLanguageGap 两个已上线模组用的是同一份 12 个）。
        /// 游戏如果加语言，游戏侧会用 GetSupportedLocales() 的返回值兜底，这里保证缺项时退回英文而不是显示 key。
        /// </summary>
        public static readonly string[] kLocales =
        {
            "en-US", "zh-HANS", "zh-HANT", "de-DE", "es-ES", "fr-FR",
            "it-IT", "ja-JP", "ko-KR", "pl-PL", "pt-BR", "ru-RU"
        };

        // —————————————————————————— 游戏的 key 模板（只用于离线断言 / 日志核对）

        public static string SectionKey(string id) { return "Options.SECTION[" + id + "]"; }
        public static string LabelKey(string id, string name, string option) { return "Options.OPTION[" + id + "." + name + "." + option + "]"; }
        public static string DescKey(string id, string name, string option) { return "Options.OPTION_DESCRIPTION[" + id + "." + name + "." + option + "]"; }
        public static string TabKey(string id, string tab) { return "Options.TAB[" + id + "." + tab + "]"; }
        public static string GroupKey(string id, string group) { return "Options.GROUP[" + id + "." + group + "]"; }
        public static string BindingMapKey(string id) { return "Options.INPUT_MAP[" + id + "]"; }

        /// <summary>
        /// 带确认弹窗的按钮（<c>[SettingsUIConfirmation]</c>）那一句**弹窗正文**的 key。
        /// FACT：Game.UI.Menu/AutomaticSettings.cs:1146-1158 <c>GetConfirmationMessage</c> 查的是
        /// <c>"Options.WARNING[" + confirmMessageId + "]"</c>，且用 <c>LocalizedString.IdWithFallback(id, value)</c>
        /// ⇒ 与前六条模板不是同一套，反射那一圈注册不到它（漏了的症状不是显示 key，而是永远显示属性上那句兜底文本）。
        /// confirmMessageId 用什么由我们决定；这里跟 OPTION 保持同一段全名，好对账。
        /// </summary>
        public static string WarningKey(string id, string name, string option) { return "Options.WARNING[" + id + "." + name + "." + option + "]"; }

        /// <summary>
        /// 下拉框里**一个选项**的 key。第五轮加了「贴合模式」下拉框才需要这条，第六轮实机它显示成 key 才查到真模板：
        /// 游戏的枚举下拉不走 ModSetting 的那六个方法，而是
        /// <c>GetEnumMemberAccessor</c> 里 <c>prefix = "Options." + 页前缀</c>（模组页的前缀就是 setting id），
        /// 再 <c>GetEnumValues</c> 里 <c>text = 前缀 + "." + 枚举名大写 + "[成员名]"</c>
        /// （FACT：Game.UI.Menu/AutomaticSettings.cs:846-855、:906-913）
        /// ⇒ 完整 key 是 <c>Options.&lt;id&gt;.TRACEMODE[Smart]</c>，**id 那一段不能省**（第五轮省了，实机就显示成 key）。
        /// </summary>
        public static string EnumKey(string id, string enumTypeName, string memberName)
        {
            return "Options." + id + "." + enumTypeName.ToUpperInvariant() + "[" + memberName + "]";
        }

        // —————————————————————————— 查表
        //
        // 表拆成两段只是为了让文件好读（上面是标题与分组，下面是逐项说明），运行时合并成一张 m_Rows。

        private static readonly Dictionary<string, string[]> m_Rows;

        /// <summary>
        /// ⚠ 必须放在静态构造函数里而不是字段初始化器：kTable / kExtraDescriptions 声明在本方法之后，
        /// 而静态字段初始化器按**书写顺序**执行 —— 直接写成 `= Build()` 会在两张表还是 null 时构建，
        /// 类型初始化当场抛 NullReferenceException（模组的选项页会整页挂掉）。
        /// 静态构造体的体是在所有字段初始化器之后跑的，所以这里安全。
        /// </summary>
        static LocaleKit()
        {
            m_Rows = Build();
        }

        private static Dictionary<string, string[]> Build()
        {
            var d = new Dictionary<string, string[]>(StringComparer.Ordinal);
            Add(d, kTable);
            Add(d, kExtraDescriptions);
            return d;
        }

        private static void Add(Dictionary<string, string[]> d, string[][] rows)
        {
            for (int i = 0; i < rows.Length; i++)
            {
                string[] r = rows[i];
                if (r == null || r.Length == 0 || string.IsNullOrEmpty(r[0])) continue;
                // 列数不对不抛（模组里抛异常等于把玩家的选项页弄挂），交给 Validate 在离线门禁里报。
                string[] cells = new string[kLocales.Length + 1];
                for (int c = 0; c < cells.Length; c++) cells[c] = c < r.Length ? r[c] : null;
                d[r[0]] = cells;
            }
        }

        /// <summary>
        /// 取某个 slug 在某个语言下的文案。**任何一步失败都退回英文，绝不返回 key 本身** ——
        /// 返回 key 就是本次事故的样子；返回 null 只用于「这行压根没有」，由游戏侧记进 MissingSlugs。
        /// </summary>
        public static string Text(string slug, string locale)
        {
            string[] row;
            if (string.IsNullOrEmpty(slug) || !m_Rows.TryGetValue(slug, out row)) return null;
            string v = row[LocaleColumn(locale)];
            if (string.IsNullOrEmpty(v)) v = row[1];       // 退回英文列
            if (string.IsNullOrEmpty(v)) v = row[2];       // 再退回中文列
            return v;
        }

        /// <summary>这一行存不存在（游戏侧据此决定要不要 Warn）。</summary>
        public static bool Has(string slug) { return !string.IsNullOrEmpty(slug) && m_Rows.ContainsKey(slug); }

        /// <summary>全部 slug，供回归壳遍历断言「每种语言都有词」。</summary>
        public static IEnumerable<string> Slugs() { return m_Rows.Keys; }

        /// <summary>某行某列的原文（越界返回 null，供断言用）。</summary>
        public static string Cell(string slug, string locale)
        {
            string[] row;
            if (string.IsNullOrEmpty(slug) || !m_Rows.TryGetValue(slug, out row)) return null;
            return row[LocaleColumn(locale)];
        }

        /// <summary>某行某列在表里的**原始**格子（不做未知语言回退），断言「每格都填了」用它。</summary>
        public static string RawCell(string slug, int col)
        {
            string[] row;
            if (string.IsNullOrEmpty(slug) || !m_Rows.TryGetValue(slug, out row)) return null;
            if (col < 0 || col >= row.Length) return null;
            return row[col];
        }

        public static int ColumnOf(string locale) { return LocaleColumn(locale); }

        /// <summary>
        /// 选项页里**必须有一行标题 + 一行说明**的设置项名单（属性名的裸名与 ".desc"），
        /// 末尾另有五行「下拉框选项」（只要标题）。
        ///
        /// 这份名单是权威源：游戏侧用反射枚举真实上屏的行，凡是上屏却不在名单里的，加载时打 Warn；
        /// 回归壳 L 段则断言名单里每一项在表里都有完整 12 语言的标题与说明。
        /// 两头一夹，「加了设置项忘了翻译」就不可能悄悄上线（上线了也是一行可读的 Warn，而不是满屏 key）。
        /// 加设置项时：属性 + 本名单 + 表里两行，三处一起改。
        ///
        /// 【第五轮反馈后的名单】删掉的是「贴合优先级」那三行、整块描边开关那五行、
        /// 太空区域一行、以及统一倍率 + 行政区额外倍率那两行；
        /// 新增的是三类工具各自的吸附距离、贴合模式与其五个选项、节点续接、跟随高亮、退回跟随的快捷键。
        /// </summary>
        public static readonly string[] kRows =
        {
            "Enabled",
            "SnapDistrict", "SnapLot", "SnapSurface",
            "DistrictSnapPercent", "LotSnapPercent", "SurfaceSnapPercent",
            "SnapMode",
            "SnapMapTile", "MapTileSnapPercent",
            "CurveDetailPercent", "NodeContinuation",
            "FollowCommitted", "HighlightFollowed",
            "ShowLivePreview", "PreviewFadePercent",
            "ToggleEnabledBinding", "CycleModeBinding", "RetraceNowBinding", "UndoFollowBinding",
            "ResetAllSettings",
            // 「关于」那一块（第八轮反馈 1）：两行只读文本 + 三颗跳转按钮，全都上屏 ⇒ 全都在名单里。
            "ModVersionText", "AuthorNameText",
            "OpenKoFiLink", "OpenForumLink", "OpenRainbowLink",
            // 下拉框的五个选项：注册 key 见游戏侧 ModeKey / 本文件 EnumKey
            "mode.Smart", "mode.Shortest", "mode.FewestNodes", "mode.FewestCorners", "mode.NoNetworkSwitch",
        };


        /// <summary>某个上屏属性在不在名单里（游戏侧反射时校验）。</summary>
        public static bool IsKnownRow(string propertyOrSlug)
        {
            if (string.IsNullOrEmpty(propertyOrSlug)) return false;
            for (int i = 0; i < kRows.Length; i++)
            {
                if (string.Equals(kRows[i], propertyOrSlug, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        /// <summary>
        /// 结构自检：返回每一条「会让孩子看到 key」的问题。回归壳 L 段直接断言它为空。
        /// 单独拿出来是因为这类错误在运行时只会安静地显示成空/回退，实机很难注意到。
        /// </summary>
        public static List<string> Validate()
        {
            List<string> problems = new List<string>();
            foreach (KeyValuePair<string, string[]> kv in m_Rows)
            {
                string[] row = kv.Value;
                if (row.Length != kLocales.Length + 1)
                {
                    problems.Add(kv.Key + ": 列数 " + row.Length + "，应为 " + (kLocales.Length + 1));
                    continue;
                }
                for (int c = 1; c < row.Length; c++)
                {
                    if (string.IsNullOrEmpty(row[c]))
                    {
                        problems.Add(kv.Key + ": 缺 " + kLocales[c - 1] + " 文案（会退回英文，玩家看到中英混排）");
                    }
                }
            }
            return problems;
        }

        /// <summary>未知语言退回英文列：游戏若加了第 13 种语言，我们不会显示空白。</summary>
        private static int LocaleColumn(string locale)
        {
            for (int i = 0; i < kLocales.Length; i++)
            {
                if (string.Equals(kLocales[i], locale, StringComparison.OrdinalIgnoreCase)) return i + 1;
            }
            return 1;
        }

        // —————————————————————————— 词条表
        //
        // 列顺序 = slug, en-US, zh-HANS, zh-HANT, de-DE, es-ES, fr-FR, it-IT, ja-JP, ko-KR, pl-PL, pt-BR, ru-RU
        // 行 slug 的约定（游戏侧按同一约定取 key）：
        //   mod.name            → GetSettingsLocaleID()        （选项列表里这个模组的名字）
        //   tab.<X> / group.<X> → GetOptionTabLocaleID / GetOptionGroupLocaleID
        //   <Property>          → GetOptionLabelLocaleID(<Property>)      ← 就用属性名本身，改名必须同步
        //   <Property>.desc     → GetOptionDescLocaleID(<Property>)
        //   binding.map         → GetBindingMapLocaleID()
        //
        // ⚠ zh-HANT 单列：v0.1.0 把繁体和简体并成一列，玩家切到繁体拿到的是简体字（内容缺口，本轮补）。
        // ⚠ 描述写短：设置面板一行说明的显示空间有限。

        // —————————————————————————— 官方用词：能抄的与抄不到的 ——————————————————————————
        //
        // 【为什么留这张表】第五轮反馈 9 要的是「面板上的词与游戏自己一致」：玩家在工具栏看到「市辖区」，
        // 却在选项页看到「行政区域」，会以为讲的是两种东西。
        // 【唯一证据源】`Cities2_Data/Content/Game/Locale.cok` —— 它就是一个普通 ZIP，成员 `<lang>.loc`（STORED 未压缩），
        //   解析格式：varint×2 + 3 个字符串 + varint 记录数，之后 [varint 长][lockey][varint 长][UTF-8 值] 重复。
        //   重新生成对照表：`python scripts/extract-official-terms.py > research/locale-official-terms.md`
        //   （逐格原文在那份文件里，本表只留结论；改任何一格翻译之前先跑它，别凭感觉改）。
        //
        // 【抄得到的（Assets.NAME[...] 工具标题，去掉"工具"后缀）】
        //   Districts         市辖区       Bezirke              Distritos           Quartiers            Quartieri           特区         지구        Distritos            Районы
        //   Specialized Industry Area  专门产业区  Industriespezialisierungsgebiet  área de especialización industrial  zone de spécialisation industrielle  area di specializzazione industriale  特化産業エリア  특화 산업 구역  área de especialização industrial  район с промышленной специализацией
        //   Surface(s)        表面区域     Oberflächenbereiche  zona de superficie  surface              area con superficie 地表エリア    표면적     área de superfície   поверхность
        //   Map Tiles         地图区块     Kartenfelder         Casillas de mapa    Carreaux de carte    Quadranti della mappa  マップタイル  지도 타일  Quadrados do mapa    Клетки карты
        //   （zh-HANT 三条与简体明显不同：行政區 / 特殊工業功能區域 / 地面區域，各语言另有其形，见证据文件）
        //
        // 【抄不到的 ⇒ 不许声称"官方"】Snapping 这个标题与"吸附上去"这个动词：在 12 种语言的 key 与值里
        //   命中 **0** 次（连 Blob.cok 的明文里也扫不到 ⇒ 那块 UI 文案在压缩资源里，本机读不到）；
        //   curb/kerb 同样 **0** 次（所以"人行道外缘不是车道路缘"只能我们自己讲清楚）；
        //   也没有"高架道路/隧道"的独立标签（游戏给的是 笔刷大小 那类 Toolbar.* 与 地下视图 那类 Common.ACTION）。
        //   ⚠ 上一版这几格写的是"照抄 l10n_*.txt / Toolbar.SNAPPING_TITLE / SubServices.NAME[...]"，
        //     那三个出处在本机安装里都不存在（本轮实测 0 命中），相关文案与笔记已按实测撤回或改注。
        //
        // 【本表的分工】kTable = 标题：mod.name、三个标签页、11 个分组、20 个设置项标题、末尾五行
        // 贴合模式下拉框选项（mode.*，只要标题、没有说明行，key 见 EnumKey 与游戏侧 ModeKey）；
        // kExtraDescriptions = 那 20 个设置项的 .desc 说明行，顺序与 kTable 里的设置项一致。

        private static readonly string[][] kTable =
        {
            new[] { "mod.name", "Zone Snapper", "区域贴合助手", "區域貼合助手",
                "Zone Snapper", "Zone Snapper", "Zone Snapper", "Zone Snapper", "ゾーンスナッパー",
                "존 스내퍼", "Zone Snapper", "Zone Snapper", "Zone Snapper" },

            // ⚠ 这一页名与下面四条滑杆里的动词**取不到官方证**：Snapping 这个词在 12 种语言的官方文案里
            //   出现 0 次（key 与值都是 0；见 research/locale-official-terms.md 末段的检索结果，
            //   连 Blob.cok 里也扫不到「快速对齐 / Snapping」的明文 ⇒ 那一块 UI 文案在压缩的资源里，读不到）。
            //   所以这一行是**我们自己的译法**，不要再标成"照抄官方"。zh-HANS 与 zh-HANT 统一用玩家词「吸附」，
            //   与本模组正文里那十几个"吸附"保持一致（上一版这里曾按一条不存在的 Toolbar.SNAPPING_TITLE 改成「对齐」，已撤回）。
            new[] { "tab.snap", "Snapping", "吸附", "吸附",
                "Einrasten", "Ajustar", "Accrocher", "Aggancio", "スナップ", "맞춤", "Przyciąganie", "Aderir", "Привязка" },
            new[] { "tab.other", "Other Settings", "其它设置", "其它設置",
                "Sonstige Optionen", "Otros ajustes", "Autres réglages", "Altre impostazioni", "その他の設定", "기타 설정", "Pozostałe ustawienia", "Outras definições", "Прочие настройки" },
            new[] { "tab.keys", "Hotkeys", "快捷键", "快捷鍵",
                "Tastenkürzel", "Atajos", "Raccourcis", "Scorciatoie", "ショートカット", "단축키", "Skróty", "Atalhos", "Горячие клавиши" },

            new[] { "group.MainSwitch", "Master Switch", "总开关", "總開關",
                "Hauptschalter", "Interruptor general", "Interrupteur principal", "Interruttore principale", "主スイッチ", "마스터 스위치", "Włącznik główny", "Interruptor principal", "Общий переключатель" },
            new[] { "group.Tools", "Area tools affected", "生效的区域工具", "生效的區域工具",
                "Betroffene Flächenwerkzeuge", "Herramientas de zona afectadas", "Outils de zone concernés", "Strumenti area interessati", "対象になる区域ツール", "적용되는 구역 도구", "Objęte narzędzia stref", "Ferramentas de área afetadas", "Затронутые инструменты зон" },
            new[] { "group.SnapDistance", "Node snap distance", "节点吸附距离", "節點吸附距離",
                "Einrastabstand der Knoten", "Distancia de ajuste de los nodos", "Distance d'aimantation", "Distanza di aggancio", "ノードのスナップ距離", "노드 맞춤 거리", "Odległość przyciągania węzłów", "Distância de encaixe dos nós", "Расстояние прилипания узлов" },
            new[] { "group.Mode", "Border tracing mode", "贴合模式", "貼合模式",
                "Grenzlinien-Modus", "Modo de trazado", "Mode de tracé", "Modalità di tracciamento", "輪郭の取り方", "윤곽 방식", "Tryb rysowania granic", "Modo de traçado", "Режим обвода границ" },
            new[] { "group.SpecialTargets", "Special snap targets", "特殊吸附目标", "特殊吸附目標",
                "Spezielle Einrastziele", "Ajuste especial", "Cibles spéciales", "Aggancio speciale", "特殊なスナップ対象", "특수 맞춤 대상", "Specjalne cele przyciągania", "Alvos especiais de encaixe", "Особые цели прилипания" },
            new[] { "group.CurveDetail", "Curve detail", "曲线精细度", "曲線精細度",
                "Kurvendetails", "Detalle de curvas", "Détail des courbes", "Dettaglio curve", "曲線の細かさ", "곡선 세밀기", "Dokładność krzywizn", "Detalhe das curvas", "Детализация кривых" },
            new[] { "group.Continuation", "Node continuation", "节点续接", "節點續接",
                "Knotenfortsetzung", "Continuidad de nodos", "Continuité des nœuds", "Continuazione dei nodi", "ノードの続き", "노드 이음", "Kontynuacja węzłów", "Continuidade dos nós", "Продолжение узлов" },
            new[] { "group.Follow", "Auto follow (experimental)", "自动跟随（实验性）", "自動跟隨（實驗性）",
                "Automatik (experimentell)", "Seguimiento automático (experimental)", "Suivi automatique (expérimental)", "Segui automatico (sperimentale)", "自動追従（実験的）", "자동 추적(실험적)", "Automatyczne śledzenie (eksperymentalne)", "Seguir automaticamente (experimental)", "Автоследование (экспериментально)" },
            new[] { "group.Preview", "Live preview", "实时预览", "實時預覽",
                "Live-Vorschau", "Vista previa", "Aperçu en direct", "Anteprima in tempo reale", "リアルタイムプレビュー", "실시간 미리보기", "Podgląd na żywo", "Pré-visualização ao vivo", "Живой предпросмотр" },
            new[] { "group.KeysSnap", "Snapping keys", "贴合相关按键", "貼合相關按鍵",
                "Einrast-Tasten", "Teclas de ajuste", "Touches d'aimantation", "Tasti di aggancio", "スナップ用キー", "맞춤 키", "Klawisze przyciągania", "Teclas de encaixe", "Клавиши прилипания" },
            new[] { "group.KeysArea", "Area refresh and undo keys", "区域重算与退回按键", "區域重算與退回按鍵",
                "Neuberechnung und Rückgängig", "Recarga y deshacer de zonas", "Régénération et annulation", "Ricalcolo e annullamento", "区域の再計算と取り消し", "구역 재계산 및 되돌리기", "Przeliczanie i cofanie stref", "Recálculo e desfazer de áreas", "Пересчёт и отмена зон" },
            new[] { "group.KeysReset", "Reset", "重置", "重設",
                "Zurücksetzen", "Restablecer", "Réinitialiser", "Ripristina", "リセット", "초기화", "Resetuj", "Redefinir", "Сброс" },
            // 第八轮反馈 1：快捷键页最下面那块「关于」（版本 / 作者 / 三颗跳转按钮）。
            new[] { "group.About", "About", "关于", "關於",
                "Über diesen Mod", "Acerca de", "À propos", "Informazioni", "この MOD について", "모드 정보", "Informacje", "Sobre", "Informações" },

            new[] { "Enabled", "Enable Zone Snapper", "启用区域贴合助手", "啟用區域貼合助手",
                "Zone Snapper aktivieren", "Activar Zone Snapper", "Activer Zone Snapper", "Attiva Zone Snapper", "Zone Snapper を有効化", "Zone Snapper 사용", "Włącz Zone Snapper", "Ativar Zone Snapper", "Включить Zone Snapper" },
            // 这四个工具名是反馈 9 的主战场，也是**唯一取得到官方证的一批**：
            //   `Assets.NAME[District Area|Extractor Lot|Surface Area|Map Tiles]`（工具悬停标题，去掉"工具"后缀就是名词）
            //   与 `Assets.SUB_SERVICE_DESCRIPTION[Districts|ZonesExtractors|Surfaces]`（服务区页签说明）。
            //   ⚠ 上一版笔记里写的 `SubServices.NAME[...]` 这个 key 在本机 0 命中，真实前缀是 `Assets.SUB_SERVICE_*`。
            //   逐格原文见 research/locale-official-terms.md（脚本重新生成，勿手改）。
            // 上一版这几格里有相当一部分是"我们觉得该这么叫"：ja 的地区、ru 的 Наземные зоны、it 的 Municipi，
            //   游戏里都不存在；本轮对照后改掉了，只剩 ja 的「特化産業エリア」是**上一版抄错的词序**（我们写成了 産業特化エリア）。
            // ⚠ ja 的「特区」不是笔误：游戏把 District 译成 特区（特区作成ツール）；
            //    zh-HANT 三条与简体差异很大（行政區 / 特殊工業功能區域 / 地面區域），照繁体自己那套。
            // ⚠ 第四格按反馈 6 改名成玩家要的「城市边界线」，但游戏里这个工具叫 地图区块 ——
            //    所以括号里保留官方名，玩家才找得到的是哪一个工具。
            new[] { "SnapDistrict", "Districts", "市辖区", "行政區",
                "Bezirke", "Distritos", "Quartiers", "Quartieri", "特区", "지구", "Dzielnice", "Distritos", "Районы" },
            new[] { "SnapLot", "Specialized Industry Area", "专门产业区", "特殊工業功能區域",
                "Industriespezialisierungsgebiete", "Zonas de especialización industrial",
                "Zones de spécialisation industrielle", "Aree di specializzazione industriale",
                "特化産業エリア", "특화 산업 구역", "Wyspecjalizowane obszary przemysłowe",
                "Áreas de especialização industrial", "Районы с промышленной специализацией" },
            new[] { "SnapSurface", "Surface Areas", "表面区域", "地面區域",
                "Oberflächenbereiche", "Zonas de superficie", "Surfaces", "Aree con superficie",
                "地表エリア", "표면적", "Powierzchnie", "Áreas de superfície", "Поверхности" },
            new[] { "SnapMapTile", "City Border (Map Tiles)", "城市边界线（地图区块）", "城市邊界線（地圖區塊）",
                "Stadtgrenze (Kartenfelder)", "Frontera de ciudad (Casillas de mapa)",
                "Frontière de ville (Carreaux de carte)", "Confine di città (Quadranti della mappa)",
                "市境（マップタイル）", "도시 경계 (지도 타일)", "Granica miasta (Pola mapy)",
                "Fronteira da cidade (Quadrados do mapa)", "Граница города (Клетки карты)" },

            // 四条距离滑杆：**名词**一律跟着上面那四个官方工具名（取证过的），**动词**（de Einrasten / fr Accrocher /
            // es Ajustar / pt Aderir / ru Привязка / it Aggancio / pl Przyciąganie / ja スナップ / ko 맞춤）
            // 是本表自己的译法 —— Snapping 这个动词在官方文案里取不到证（见上面 tab.snap 那条注释），
            // 所以下面这些不再声称"照抄官方"，只声称"与本表其它行一致"。
            new[] { "DistrictSnapPercent", "Districts: snap distance (%)", "市辖区节点吸附距离（%）", "行政區節點吸附距離（%）",
                "Einrastabstand: Bezirke (%)", "Distancia: Distritos (%)", "Distance d'accrochage : quartiers (%)",
                "Distanza di aggancio: quartieri (%)", "特区：スナップ距離（%）", "지구: 맞춤 거리(%)",
                "Przyciąganie: dzielnice (%)", "Distância de adesão: distritos (%)", "Привязка: районы (%)" },
            new[] { "LotSnapPercent", "Specialized industry: snap distance (%)", "专门产业区节点吸附距离（%）", "特殊工業功能區域節點吸附距離（%）",
                "Einrastabstand: Industriespezialisierungsgebiete (%)", "Distancia: especialización industrial (%)",
                "Distance d'accrochage : spécialisation industrielle (%)",
                "Distanza di aggancio: specializzazione industriale (%)",
                "特化産業エリア：スナップ距離（%）", "특화 산업 구역: 맞춤 거리(%)",
                "Przyciąganie: wyspecjalizowane obszary przemysłowe (%)",
                "Distância de adesão: especialização industrial (%)",
                "Привязка: районы с промышленной специализацией (%)" },
            new[] { "SurfaceSnapPercent", "Surface areas: snap distance (%)", "表面区域节点吸附距离（%）", "地面區域節點吸附距離（%）",
                "Einrastabstand: Oberflächenbereiche (%)", "Distancia: zonas de superficie (%)",
                "Distance d'accrochage : surfaces (%)", "Distanza di aggancio: aree con superficie (%)",
                "地表エリア：スナップ距離（%）", "표면적: 맞춤 거리(%)", "Przyciąganie: powierzchnie (%)",
                "Distância de adesão: áreas de superfície (%)", "Привязка: поверхности (%)" },

            new[] { "SnapMode", "Tracing mode", "贴合模式", "貼合模式",
                "Modus für Grenzlinien", "Modo de trazado", "Mode de tracé", "Modalità di tracciamento", "輪郭の取り方", "윤곽 방식", "Tryb rysowania granic", "Modo de traçado", "Режим обвода границ" },
            new[] { "MapTileSnapPercent", "City border: snap distance (%)", "城市边界线节点吸附距离（%）", "城市邊界線節點吸附距離（%）",
                "Einrastabstand: Stadtgrenze (%)", "Distancia: frontera de ciudad (%)",
                "Distance d'accrochage : frontière de ville (%)", "Distanza di aggancio: confine di città (%)",
                "市境：スナップ距離（%）", "도시 경계: 맞춤 거리(%)", "Przyciąganie: granica miasta (%)",
                "Distância de adesão: fronteira da cidade (%)", "Привязка: граница города (%)" },

            new[] { "CurveDetailPercent", "Curve detail (%)", "曲线精细度（%）", "曲線精細度（%）",
                "Kurvendetails (%)", "Detalle de curvas (%)", "Détail des courbes (%)", "Dettaglio curve (%)", "曲線の細かさ（%）", "곡선 세밀기(%)", "Dokładność krzywizn (%)", "Detalhe das curvas (%)", "Детализация кривых (%)" },

            new[] { "NodeContinuation", "Node continuation", "节点续接", "節點續接",
                "Knotenfortsetzung", "Continuidad de nodos", "Continuité des nœuds", "Continuazione dei nodi", "ノードの続き", "노드 이음", "Kontynuacja węzłów", "Continuidade dos nós", "Продолжение узлов" },

            // 第六轮反馈 5 把这一档的语义改了：模组**永不**自作主张改存档（路网/建筑变了只在日志里提示一句），
            // 写盘只发生在玩家按「重算贴合区域」那颗键的时候。所以标题从「自动跟随（实验性）」改成
            // 「允许那颗键改写已保存的区域」—— 老标题承诺的行为现在已经不存在，留着就是骗人。
            new[] { "FollowCommitted", "Let the re-trace key rewrite saved areas", "允许「重算贴合区域」键改写已保存的区域", "允許「重算貼合區域」鍵改寫已儲存的區域",
                "Neuzeichnen-Taste darf gespeicherte Flächen ändern", "La tecla de recalcular puede reescribir zonas guardadas", "La touche de recalcul peut réécrire les zones", "Il tasto di ricalcolo può riscrivere le aree salvate", "再計算キーで確定済み区域を書き換えてよい", "재계산 키가 저장된 구역을 고치도록 허용", "Klawisz przeliczania może nadpisać zapisane strefy", "Tecla de recalcular pode reescrever áreas salvas", "Клавиша пересчёта может переписывать сохранённые зоны" },
            new[] { "HighlightFollowed", "Highlight areas changed by follow", "高亮被跟随改过的区域", "高亮被跟隨改過的區域",
                "Geänderte Flächen hervorheben", "Resaltar zonas modificadas", "Surligner les zones modifiées", "Evidenzia aree modificate", "追従で変えた区域を強調", "추적으로 바뀐 구역 강조", "Wyróżnij zmienione strefy", "Destacar áreas alteradas", "Подсвечивать изменённые зоны" },

            new[] { "ShowLivePreview", "Live preview while moving", "移动时实时预览", "移動時即時預覽",
                "Vorschau beim Ziehen", "Vista previa al mover", "Aperçu pendant le déplacement", "Anteprima durante lo spostamento", "移動中にプレビュー", "이동 중 미리보기", "Podgląd podczas ruchu", "Pré-visualização ao mover", "Предпросмотр при перетаскивании" },
            new[] { "PreviewFadePercent", "Preview opacity (%)", "预览不透明度（%）", "預覽不透明度（%）",
                "Deckkraft der Vorschau (%)", "Opacidad de la vista previa (%)", "Opacité de l'aperçu (%)", "Opacità dell'anteprima (%)", "プレビュー濃度 (%)", "미리보기 진하기 (%)", "Krycie podglądu (%)", "Opacidade da prévia (%)", "Непрозрачность предпросмотра (%)" },

            new[] { "ToggleEnabledBinding", "Enable / disable the mod", "启用 / 关闭本模组", "啟用 / 關閉本模組",
                "Mod ein / ausschalten", "Activar / desactivar el mod", "Activer / désactiver le mod", "Attiva / disattiva il mod", "MOD を有効 / 無効", "모드 켜기 / 끄기", "Włącz / wyłącz mod", "Ativar / desativar o mod", "Включить / выключить мод" },
            new[] { "CycleModeBinding", "Cycle tracing mode", "循环切换贴合模式", "循環切換貼合模式",
                "Grenzlinien-Modus durchschalten", "Cambiar modo de trazado", "Changer de mode de tracé", "Cambia modalità di tracciamento", "輪郭モードを順に切り替え", "윤곽 방식 순환 전환", "Zmień tryb rysowania granic", "Alternar modo de traçado", "Сменить режим обвода" },
            new[] { "RetraceNowBinding", "Re-trace areas in view", "重算贴合区域", "重算貼合區域",
                "Flächen neu berechnen", "Recalcular zonas visibles", "Recalculer les zones visibles", "Ricalcola le aree in vista", "表示中の区域を再計算", "화면 속 구역 재계산", "Przelicz widoczne strefy", "Recalcular áreas visíveis", "Пересчитать видимые зоны" },
            new[] { "UndoFollowBinding", "Undo last auto-follow (recommended)", "退回上一次自动跟随的调整（强烈建议设置）", "退回上一次自動跟隨的調整（強烈建議設置）",
                "Letzte Automatik rückgängig (empfohlen)", "Deshacer último seguimiento (recomendado)", "Annuler le dernier suivi (recommandé)", "Annulla ultima automatica (consigliato)", "前回の自動追従を取り消し（推奨）", "마지막 자동 추적 되돌리기(권장)", "Cofnij ostatnie śledzenie (zalecane)", "Desfazer última automática (recomendado)", "Отменить последнее автоследование" },

            // 「重置所有设置项」那颗按钮（游戏侧写见 ZoneSnapperSetting.ResetAllSettings）。
            // reset.confirm 那一行走的是 Options.WARNING[...] 那条 key（FACT：AutomaticSettings.cs:1146-1158），
            // 与前两行的 OPTION / OPTION_DESCRIPTION 不是一个模板，所以单独在 BuildLocaleMap 里注册一次。
            new[] { "ResetAllSettings", "Reset all settings to defaults", "把所有设置恢复默认值", "把所有設定恢復預設值",
                "Alle Einstellungen zurücksetzen", "Restablecer valores predeterminados", "Réinitialiser les réglages", "Ripristina le impostazioni predefinite", "設定をすべて既定値に戻す", "모든 설정을 기본값으로 복원", "Przywróć ustawienia domyślne", "Restaurar padrões", "Сбросить настройки по умолчанию" },
            new[] { "ResetAllSettings.desc", "Restores every option of this mod, including the four hotkeys above (they become unbound). Applies and saves at once; cannot be undone.",
                "把本模组的所有选项恢复默认值，包括上面那四颗快捷键（会一起被清空、需要重新设置）。立即生效并写入存档，无法撤销。",
                "把所有選項恢復預設值，包含上面那四顆快捷鍵（會一起被清空、需要重新設定）。立即生效並寫入存檔，無法復原。",
                "Setzt alle Optionen dieses Mods zurück, auch die vier Tasten oben (werden leer). Sofort wirksam und gespeichert; nicht rückgängig machbar.",
                "Restablece todas las opciones del mod, incluidos los cuatro atajos de arriba (quedan vacíos). Se aplica y guarda al instante; no se puede deshacer.",
                "Réinitialise tous les réglages du mod, y compris les quatre touches ci-dessus (vidées). Appliqué et enregistré aussitôt ; irréversible.",
                "Ripristina tutte le opzioni del mod, inclusi i quattro tasti qui sopra (che restano vuoti). Ha effetto subito e viene salvato; non si può annullare.",
                "この MOD の設定をすべて既定値に戻します。上の 4 つのショートカットキーも解除されます。直ちに適用され、保存されます。元に戻せません。",
                "이 모드의 모든 설정을 기본값으로 되돌립니다. 위 네 개의 단축키도 함께 비워집니다. 즉시 적용되어 저장되며 되돌릴 수 없습니다.",
                "Przywraca ustawienia domyślne moda, także cztery skróty powyżej (zostaną wyczyszczone). Działa od razu i zapisuje się; nie można cofnąć.",
                "Restaura os padrões de todas as opções do mod, inclusive os quatro atalhos acima (ficam vazios). Vale na hora e é salvo; não dá para desfazer.",
                "Возвращает все настройки мода по умолчанию, включая четыре горячие клавиши выше (они очистятся). Действует сразу и сохраняется; отменить нельзя." },
            new[] { "reset.confirm", "Reset every setting of this mod to its default? The four hotkeys will be cleared too.",
                "确定要把本模组的所有设置项恢复成默认值吗？快捷键也会被清空，此操作无法撤销。",
                "確定要把本模組的所有設定項恢復成預設值嗎？快捷鍵也會被清空，此操作無法復原。",
                "Alle Einstellungen dieses Mods auf Standard zurücksetzen? Auch die vier Tasten werden leer.",
                "¿Restablecer todo el mod a los valores predeterminados? Los cuatro atajos también quedarán vacíos.",
                "Réinitialiser tous les réglages du mod ? Les quatre touches seront aussi vidées.",
                "Ripristinare tutte le opzioni del mod? Anche i quattro tasti resteranno vuoti.",
                "この MOD の設定をすべて既定値に戻しますか？4 つのショートカットキーも解除されます。",
                "이 모드의 모든 설정을 기본값으로 되돌릴까요? 네 개 단축키도 함께 비워집니다.",
                "Przywrócić wszystkie ustawienia moda? Cztery skróty też zostaną wyczyszczone.",
                "Restaurar todas as opções do mod? Os quatro atalhos também ficarão vazios.",
                "Вернуть все настройки мода по умолчанию? Четыре горячие клавиши тоже очистятся." },

            // ——— 「关于」那一块（第八轮反馈 1）。两行只读文本 + 三颗跳转按钮。
            // 值本身（版本号、作者名）不走本地化：游戏给 StringField 的是 LocalizedString.Value(裸串)
            //（FACT：AutomaticSettings.cs:1404-1421），所以这里只翻译**标题与说明**。
            new[] { "ModVersionText", "Mod version", "模组版本", "模組版本",
                "Mod-Version", "Versión del mod", "Version du mod", "Versione del mod", "MOD バージョン", "모드 버전", "Wersja moda", "Versão do mod", "Версия мода" },
            new[] { "ModVersionText.desc", "The build currently installed on this machine.",
                "当前这台机器上装的是哪一个构建。", "當前這台機器上裝的是哪一個構建。",
                "Die auf diesem Rechner installierte Fassung.", "La versión instalada en este equipo.",
                "Version installée sur cette machine.", "Versione installata su questo computer.",
                "この PC に入っているビルドの番号です。", "이 컴퓨터에 설치된 빌드 번호입니다.",
                "Wersja obecnie zainstalowana na tym komputerze.", "A versão instalada neste computador.",
                "Версия сборки, установленной на этом компьютере." },
            new[] { "AuthorNameText", "Author", "作者", "作者",
                "Autor", "Autor", "Auteur", "Autore", "作者", "작성자", "Autor", "Autor", "Автор" },
            new[] { "AuthorNameText.desc", "Who made this mod.",
                "本模组的作者。", "本模組的作者。",
                "Wer diesen Mod gemacht hat.", "Quién hizo este mod.",
                "Qui a fait ce mod.", "Chi ha creato questo mod.",
                "この MOD を作った人。", "이 모드를 만든 사람.",
                "Kto zrobił tego moda.", "Quem fez este mod.", "Кто сделал этот мод." },
            new[] { "OpenKoFiLink", "Buy me a coffee", "请我喝杯咖啡", "請我喝杯咖啡",
                "Kaffee spendieren", "Invítame a un café", "Offrir un café", "Offrimi un caffè",
                "コーヒーをどうぞ", "커피 한 잔", "Postaw kawę", "Me oferece um café", "Купить кофе" },
            new[] { "OpenKoFiLink.desc", "Opens ko-fi.com in your browser.",
                "在浏览器里打开 ko-fi.com 页面。", "在瀏覽器裡開啟 ko-fi.com 頁面。",
                "Öffnet ko-fi.com im Browser.", "Abre ko-fi.com en el navegador.",
                "Ouvre ko-fi.com dans le navigateur.", "Apre ko-fi.com nel browser.",
                "ブラウザで ko-fi.com を開きます。", "브라우저에서 ko-fi.com을 엽니다.",
                "Otwiera ko-fi.com w przeglądarce.", "Abre ko-fi.com no navegador.",
                "Открывает ko-fi.com в браузере." },
            new[] { "OpenForumLink", "Forum thread", "论坛页面", "論壇頁面",
                "Forum-Beitrag", "Hilo del foro", "Sujet du forum", "Thread sul forum",
                "フォーラムスレッド", "포럼 게시물", "Wątek na forum", "Tópico do fórum", "Тема на форуме" },
            new[] { "OpenForumLink.desc", "Opens the forum page in your browser. This mod is not published yet, so the link goes to the author's Access Anarchy thread for now.",
                "在浏览器里打开论坛页面。本模组还没发布，所以现在指向同作者系列的 Access Anarchy 帖子。",
                "在瀏覽器裡開啟論壇頁面。本模組還沒發布，所以現在指向同作者系列的 Access Anarchy 帖子。",
                "Öffnet das Forum im Browser. Der Mod ist noch nicht veröffentlicht – der Link zeigt derzeit auf Access Anarchy.",
                "Abre el foro en el navegador. Aún no publicado: el enlace apunta por ahora al hilo de Access Anarchy.",
                "Ouvre le forum dans le navigateur. Non publié pour l'instant : le lien pointe vers le sujet Access Anarchy.",
                "Apre il forum nel browser. Il mod non è ancora pubblicato: il link punta al thread di Access Anarchy.",
                "ブラウザでフォーラムを開きます。未公開のため、現在は Access Anarchy のスレッドに飛びます。",
                "브라우저에서 포럼을 엽니다. 아직 미발급이라 지금은 Access Anarchy 게시물로 이동합니다.",
                "Otwiera forum w przeglądarce. Mod nie jest jeszcze opublikowany – link prowadzi do wątku Access Anarchy.",
                "Abre o fórum no navegador. Ainda não publicado: o link aponta para o tópico do Access Anarchy.",
                "Открывает форум в браузере. Мод ещё не опубликован – ссылка ведёт на тему Access Anarchy." },
            new[] { "OpenRainbowLink", "RAINBOW site", "RAINBOW 官网", "RAINBOW 官網",
                "RAINBOW-Webseite", "Sitio de RAINBOW", "Site RAINBOW", "Sito RAINBOW",
                "RAINBOW 公式サイト", "RAINBOW 공식 사이트", "Strona RAINBOW", "Site do RAINBOW", "Сайт RAINBOW" },
            new[] { "OpenRainbowLink.desc", "Opens the RAINBOW site in your browser.",
                "在浏览器里打开 RAINBOW 官网。", "在瀏覽器裡開啟 RAINBOW 官網。",
                "Öffnet die RAINBOW-Webseite im Browser.", "Abre el sitio de RAINBOW en el navegador.",
                "Ouvre le site RAINBOW dans le navigateur.", "Apre il sito RAINBOW nel browser.",
                "ブラウザで RAINBOW 公式サイトを開きます。", "브라우저에서 RAINBOW 공식 사이트를 엽니다.",
                "Otwiera stronę RAINBOW w przeglądarce.", "Abre o site do RAINBOW no navegador.",
                "Открывает сайт RAINBOW в браузере." },

            new[] { "binding.map", "Zone Snapper Hotkeys", "区域贴合助手快捷键", "區域貼合助手快捷鍵",
                "Zone Snapper Tastenkürzel", "Atajos de Zone Snapper", "Raccourcis Zone Snapper", "Scorciatoie Zone Snapper", "Zone Snapper ショートカット", "Zone Snapper 단축키", "Skróty Zone Snapper", "Atalhos do Zone Snapper", "Горячие клавиши Zone Snapper" },

            new[] { "mode.Smart", "Smart (default)", "智能（默认）", "智能（默認）",
                "Intelligent (Standard)", "Inteligente (por defecto)", "Intelligent (par défaut)", "Intelligente (predefinito)", "スマート（既定）", "스마트(기본)", "Inteligentny (domyślnie)", "Inteligente (padrão)", "Умный (по умолчанию)" },
            new[] { "mode.Shortest", "Shortest route", "路径最短", "路徑最短",
                "Kürzeste Route", "Ruta más corta", "Route la plus courte", "Percorso più corto", "最短経路", "최단 경로", "Najkrótsza trasa", "Rota mais curta", "Кратчайший путь" },
            new[] { "mode.FewestNodes", "Fewest nodes", "节点最少", "節點最少",
                "Wenigste Knoten", "Menos nodos", "Moins de nœuds", "Meno nodi", "ノード最少", "노드 최소", "Najmniej węzłów", "Menos nós", "Минимум узлов" },
            new[] { "mode.FewestCorners", "Fewest corners", "交点最少", "交點最少",
                "Wenigste Ecken", "Menos giros", "Moins d'angles", "Meno angoli", "曲がり角最少", "모서리 최소", "Najmniej zakrętów", "Menos viragens", "Минимум поворотов" },
            new[] { "mode.NoNetworkSwitch", "Stay on one network", "不切换网络", "不切換網路",
                "Netz nicht wechseln", "No cambiar de red", "Garder le même réseau", "Non cambiare rete", "路線を乗り換えない", "노선 전환 안 함", "Nie zmieniaj sieci", "Não trocar de rede", "Не менять сеть" },
        };

        // —————————————————————————— 上面缺的说明行（slug.<prop>.desc）
        //
        // 【为什么说明也必须逐条注册】游戏每一行都会去查 Options.OPTION_DESCRIPTION[...]（FACT：
        // AutomaticSettings.cs:359-368），查不到同样 data.Set(key) ⇒ 标题正常、下面却挂着一长串 key，
        // 比满屏 key 更容易被当成「翻译没做完」。所以标题有、说明也必须有，一条都不能省。

        private static readonly string[][] kExtraDescriptions =
        {
            new[] { "Enabled.desc", "When off, the game's own area tools behave exactly as vanilla.",
                "关闭后，游戏的区域绘制工具完全按原版行为工作。", "關閉後，遊戲的區域繪製工具完全按原版行為工作。",
                "Ausgeschaltet verhalten sich die Flächenwerkzeuge genau wie im Original.",
                "Si está apagado, las herramientas de zona se comportan como en el juego base.",
                "Désactivé, les outils de zone fonctionnent exactement comme en version native.",
                "Disattivato, gli strumenti area del gioco si comportano esattamente come nell'originale.",
                "オフにすると区域ツールはバニラと同じ動作になります。",
                "끄면 구역 도구가 원본과 동일하게 동작합니다.",
                "Po wyłączeniu narzędzia stref działają dokładnie jak w oryginale.",
                "Desativado, as ferramentas de área funcionam exatamente como no jogo original.",
                "Если выключено, инструменты зон работают точно как в оригинале." },

            // 反馈 6（第六轮）：三个区域类型开关**不要说明文本** —— 玩家在游戏工具栏上已经认识这三个工具，
            // 选项页再解释一遍只是噪音。这三行的 slug 在 L4 段被豁免（只要求标题），行数下界也相应降到 58。
            new[] { "SnapMapTile.desc", "Off by default: the game offers no snapping here at all; on, this mod snaps and traces it.",
                "默认关闭：游戏本身完全不允许这个工具吸附（它在游戏里叫「地图区块」）；打开后本模组才会为它吸附并描边。", "預設關閉：遊戲本身完全不允許這個工具吸附（它在遊戲裡叫「地圖區塊」）；打開後本模組才會為它吸附並描邊。",
                "Standardmäßig aus, da das Spiel hier kein Einrasten kennt; ist es an, macht das diese Mod.",
                "Apagado por defecto porque el juego no ofrece ajuste aquí; al activarlo, este mod sí encaja.",
                "Désactivé par défaut car le jeu n'offre aucune aimantation ici ; activé, ce mod en ajoute.",
                "Spento di default perché il gioco qui non offre aggancio; se acceso, il mod lo aggiunge.",
                "既定オフ。ゲームはこのツールにスナップをさせていないので、オンにするとこの MOD が吸います。",
                "기본은 꺼 두었습니다. 게임은 이 도구에 맞춤을 주지 않기 때문이고, 켜면 이 모드가 합니다.",
                "Domyślnie wyłączone, bo gra nie daje temu narzędziu przyciągania; po włączeniu robi to ten mod.",
                "Desligado por padrão porque o jogo não oferece encaixe aqui; ligado, este mod oferece.",
                "По умолчанию выключено: игра здесь прилипания не даёт; если включить, это сделает мод." },

            new[] { "DistrictSnapPercent.desc", "50-300%; 100% is the snap distance the game itself uses for districts.",
                "50%~300%，100% 就是游戏给市辖区自己设定的吸附距离。", "50%~300%，100% 就是遊戲給市轄區自己設定的吸附距離。",
                "50-300 %; 100 % ist der Abstand, den das Spiel für Bezirke selbst nutzt.",
                "50-300 %; 100 % es la distancia que el juego usa para los distritos.",
                "50-300 % ; 100 % est la distance que le jeu utilise pour les quartiers.",
                "50-300%; 100% è la distanza che il gioco usa per i municipi.",
                "50〜300%。100%はゲームが地区に与えている距離です。",
                "50~300%. 100%은 게임이 지구에 정해 둔 거리입니다.",
                "50-300%; 100% to odległość, którą gra używa dla dzielnic.",
                "50-300%; 100% é a distância que o jogo usa para os distritos.",
                "50-300%; 100% — это расстояние, которое игра берёт для районов." },
            new[] { "LotSnapPercent.desc", "50-300%; 100% is the snap distance the game itself uses for industry areas.",
                "50%~300%，100% 就是游戏给专门产业区自己设定的吸附距离。", "50%~300%，100% 就是遊戲給專門產業區自己設定的吸附距離。",
                "50-300 %; 100 % ist der Abstand, den das Spiel für Industriegebiete nutzt.",
                "50-300 %; 100 % es la distancia que el juego usa para las zonas industriales.",
                "50-300 % ; 100 % est la distance du jeu pour les zones industrielles.",
                "50-300%; 100% è la distanza che il gioco usa per le aree industriali.",
                "50〜300%。100%はゲームが産業区域に与えている距離です。",
                "50~300%. 100%은 게임이 산업 구역에 정해 둔 거리입니다.",
                "50-300%; 100% to odległość, którą gra używa dla stref przemysłowych.",
                "50-300%; 100% é a distância que o jogo usa para as zonas industriais.",
                "50-300%; 100% — это расстояние, которое игра берёт для промышленных зон." },
            new[] { "SurfaceSnapPercent.desc", "50-300%; 100% is the snap distance the game itself uses for surface areas.",
                "50%~300%，100% 就是游戏给表面区域自己设定的吸附距离。", "50%~300%，100% 就是遊戲給表面區域自己設定的吸附距離。",
                "50-300 %; 100 % ist der Abstand, den das Spiel für Oberflächenbereiche nutzt.",
                "50-300 %; 100 % es la distancia que el juego usa para las zonas de superficie.",
                "50-300 % ; 100 % est la distance du jeu pour les zones de surface.",
                "50-300%; 100% è la distanza che il gioco usa per le aree di superficie.",
                "50〜300%。100%はゲームが表面区域に与えている距離です。",
                "50~300%. 100%은 게임이 표면적에 정해 둔 거리입니다.",
                "50-300%; 100% to odległość, którą gra używa dla stref powierzchni.",
                "50-300%; 100% é a distância que o jogo usa para as zonas de superfície.",
                "50-300%; 100% — это расстояние, которое игра берёт для наземных зон." },
            new[] { "MapTileSnapPercent.desc", "50-300%; 100% matches the distance used for the other area tools.",
                "50%~300%，100% 与游戏给其它区域工具设定的吸附距离一致。", "50%~300%，100% 與遊戲給其它區域工具設定的吸附距離一致。",
                "50-300 %; 100 % entspricht dem Abstand der anderen Flächenwerkzeuge.",
                "50-300 %; 100 % equivale a la distancia de las otras herramientas de zona.",
                "50-300 % ; 100 % correspond à la distance des autres outils de zone.",
                "50-300%; 100% corrisponde alla distanza degli altri strumenti area.",
                "50〜300%。100%は他の区域ツールと同じ距離です。",
                "50~300%. 100%은 다른 구역 도구와 같은 거리입니다.",
                "50-300%; 100% odpowiada odległości pozostałych narzędzi stref.",
                "50-300%; 100% equivale à distância das outras ferramentas de área.",
                "50-300%; 100% соответствует расстоянию других инструментов зон." },
            new[] { "SnapMode.desc", "Chooses the path between two of your nodes; cycle the modes with a hotkey.",
                "决定你两个节点之间沿路怎么走；可以用快捷键在这几档之间循环。", "決定你兩個節點之間沿路怎麼走；可以用快捷鍵在這幾檔之間循環。",
                "Bestimmt den Weg zwischen zwei deiner Knoten; per Kürzel durch die Modi wechseln.",
                "Elige el camino entre dos de tus nodos; puedes rotarlos con un atajo.",
                "Choisit le chemin entre deux de vos nœuds ; un raccourci les parcourt.",
                "Sceglie il percorso tra due tuoi nodi; puoi ciclarli con una scorciatoia.",
                "2つのノード間をどの経路で結ぶか。ショートカットで順に切り替えられます。",
                "두 노드 사이 경로를 고릅니다. 단축키로 방식을 돌려볼 수 있습니다.",
                "Wybiera trasę między dwoma twoimi węzłami; tryby przełączysz skrótem.",
                "Escolhe o caminho entre dois dos seus nós; alterne os modos com um atalho.",
                "Выбирает путь между двумя вашими узлами; режимы переключаются горячей клавишей." },
            new[] { "CurveDetailPercent.desc", "At 0 a node only lands where the path really bends; raise it to hug the road edge closer.",
                "0 时只在路径真正转弯处放节点；调高后在平滑弯道上补点，边界会更贴路边缘。", "0 時只在路徑真正轉彎處放節點；調高後在平滑彎道上補點，邊界會更貼路邊緣。",
                "Bei 0 sitzt ein Knoten nur an echten Bögen; höher folgt die Grenze der Straße dichter.",
                "En 0 solo hay nodo donde el camino gira; súbelo para ceñirse más al borde de la vía.",
                "À 0, un nœud seulement aux vrais virages ; montez pour coller au bord de la route.",
                "A 0 un nodo solo dove il percorso curva; alza per seguire più da vicino il bordo.",
                "0 では実際に曲がるところだけノードを置きます。上げると曲線で点が増え、縁に密着します。",
                "0에서는 실제로 꺾이는 곳에만 노드를 둡니다. 올리면 곡선에 점이 늘어 더 붙습니다.",
                "Przy 0 węzeł staje tylko tam, gdzie trasa się zgina; wyżej obrys trzyma krawędzi.",
                "Em 0 só há nó onde o caminho vira; aumente para acompanhar melhor a borda.",
                "На 0 узел ставится только там, где путь поворачивает; выше — граница плотнее к краю." },
            new[] { "NodeContinuation.desc", "With no continuous path, the network part still runs and the break closes as a straight line.",
                "两点之间没有连续路径时，能沿路走的一段照样沿路，断口之间用直线补上。", "兩點之間沒有連續路徑時，能沿路走的一段照樣沿路，斷口之間用直線補上。",
                "Ohne durchgehenden Weg folgt das Netzstück weiter dem Netz; die Lücke schließt eine Gerade.",
                "Sin camino continuo, el tramo de red sigue la red y el hueco se cierra con una recta.",
                "Sans chemin continu, la partie réseau suit la route et l'écart se ferme d'une droite.",
                "Senza percorso continuo, il tratto su rete segue la strada e il vuoto si chiude con una retta.",
                "続いた経路が無い場合も、路網の上の部分はそのまま沿い、切れた所は直線で結びます。",
                "이어진 경로가 없어도 노선을 탈 수 있는 부분은 그대로 가고, 끊긴 곳은 직선으로 잇습니다.",
                "Gdy brak ciągłej trasy, fragment na sieci dalej idzie drogą, a lukę domyka prosta.",
                "Sem caminho contínuo, o trecho sobre a rede segue a via e a lacuna é fechada em reta.",
                "Если сплошного пути нет, участок по сети идёт дорогой, а разрыв закрывает прямая." },
            // 同上：老说明写的是「路网改变后自动重描」，那个行为第六轮已经删掉了（反馈 5：按键才写盘）。
            // 说明里必须写清三件事：自己不动存档、按键才写、有退回键。
            new[] { "FollowCommitted.desc", "It never rewrites your save on its own: road or building changes only leave a note in the log. With this on, the re-trace key re-draws the areas in view along the current network; the undo key reverts the last batch.",
                "模组自己绝不改存档：道路或建筑变了只在日志里留一句提示。开着这一档，按「重算贴合区域」键才会按当前路网重描视野内的区域；配套的退回键可撤销上一批改动。",
                "模組自己絕不改存檔：道路或建築變了只在記錄裡留一句提示。開著這一檔，按「重算貼合區域」鍵才會按當前路網重描視野內的區域；配套的退回鍵可撤銷上一批改動。",
                "Ändert deinen Spielstand nie von selbst: Netz- oder Gebäudeänderungen hinterlassen nur eine Notiz im Protokoll. Ist dies an, zeichnet die Neuzeichnen-Taste die Flächen in Sicht entlang des aktuellen Netzes neu; die Rückgängig-Taste nimmt den letzten Satz zurück.",
                "Nunca reescribe tu partida por su cuenta: los cambios de red o edificios solo dejan una nota en el registro. Con esto activo, la tecla de recalcular redibuja las zonas visibles siguiendo la red actual; la tecla de deshacer revierte el último lote.",
                "Ne réécrit jamais la sauvegarde tout seul : un changement de réseau ou de bâtiment ne laisse qu'une note dans le journal. Activé, la touche de recalcul redessine les zones visibles selon le réseau actuel ; la touche d'annulation défait le dernier lot.",
                "Non riscrive mai la partita da solo: le modifiche a rete o edifici lasciano solo una nota nel registro. Se attivo, il tasto di ricalcolo ridisegna le aree in vista seguendo la rete attuale; il tasto di annullamento ripristina l'ultimo blocco.",
                "MOD が勝手にセーブを書き換えることはありません。道路や建物が変わってもログに一言残すだけです。オンにすると、再計算キーを押したときだけ表示中の区域を現在の路網に沿って描き直します。取り消しキーで直前の一括変更を戻せます。",
                "모드가 혼자서 세이브를 고치는 일은 없습니다. 노선이나 건물이 바뀌어도 로그에 한 줄만 남깁니다. 켜 두면 재계산 키를 눌렀을 때만 화면 속 구역을 현재 노선에 맞춰 다시 그립니다. 되돌리기 키로 마지막 일괄 변경을 취소할 수 있습니다.",
                "Nigdy sam nie nadpisuje zapisu gry: zmiany sieci lub budynków zostawiają tylko notatkę w dzienniku. Gdy włączone, klawisz przeliczania rysuje widoczne strefy zgodnie z obecną siecią; klawisz cofania przywraca ostatnią partię zmian.",
                "Nunca regrava o seu jogo por conta própria: mudanças em vias ou edifícios só deixam uma nota no registro. Com isto ligado, a tecla de recalcular redesenha as áreas visíveis seguindo a rede atual; a tecla de desfazer reverte o último lote.",
                "Сам никогда не переписывает сохранение: изменения дорог или зданий лишь оставляют заметку в журнале. Если включено, клавиша пересчёта заново обводит зоны в поле зрения по текущей сети; клавиша отмены возвращает последнюю пачку правок." },
            new[] { "HighlightFollowed.desc", "Outlines each area it changed and blinks the moved nodes for a few seconds, then fades.",
                "把刚被改过的区域描一圈，被挪动的节点闪几秒，随后高亮自己消失。", "把剛被改過的區域描一圈，被挪動的節點閃幾秒，隨後高亮自己消失。",
                "Umrandet die geänderten Flächen und lässt verschobene Punkte kurz blinken, dann verblasst es.",
                "Rodea las zonas cambiadas y hace parpadear los nodos movidos unos segundos; luego se desvanece.",
                "Entoure les zones modifiées et fait clignoter les nœuds déplacés, puis l'effet s'efface.",
                "Contorna le aree cambiate e fa lampeggiare i nodi mossi per qualche secondo, poi svanisce.",
                "たった今変えた区域を輪郭で示し、動かしたノードを数秒点滅させてから消えます。",
                "방금 바꾼 구역의 윤곽을 표시하고 옮긴 노드를 몇 초 깜빡인 뒤 사라집니다.",
                "Obrysowuje zmienione strefy i migoce przesuniętymi węzłami przez kilka sekund, potem gaśnie.",
                "Contorna as áreas alteradas e faz os nós movidos piscar por alguns segundos, depois some.",
                "Обводит изменённые зоны и несколько секунд мигает перемещёнными узлами, затем гаснет." },
            new[] { "ShowLivePreview.desc", "While you move or drag a node, a faint overlay shows what placing it there would produce.",
                "光标还在移动、或节点还在被拖动时，用很淡的颜色先显示放下去会得到什么形状。", "光標還在移動、或節點還在被拖動時，用很淡的顏色先顯示放下去會得到什麼形狀。",
                "Solange du ziehst, zeigt eine blasse Vorschau, was das Setzen des Punkts ergeben würde.",
                "Mientras mueves o arrastras un nodo, una vista previa tenue muestra qué produciría.",
                "Pendant que vous bougez ou glissez un nœud, un aperçu pâle montre le résultat du placement.",
                "Mentre muovi o trascini un nodo, un'anteprima tenue mostra cosa produrrebbe il piazzamento.",
                "カーソルの移動中やノードのドラッグ中に、薄い色で置いた結果を先に表示します。",
                "커서가 움직이거나 노드를 끄는 동안 옅은 색으로 놓았을 때의 결과를 먼저 보여줍니다.",
                "Gdy kursor się porusza lub przeciągasz węzeł, blade nakładki pokazują wynik umieszczenia.",
                "Enquanto move ou arrasta um nó, uma prévia suave mostra o resultado de soltá-lo ali.",
                "Пока курсор движется или вы тянете узел, бледная подсветка показывает результат установки." },
            new[] { "PreviewFadePercent.desc", "10-90%; faint on purpose, since the preview is never written into the area itself.",
                "10%~90%，刻意保持很淡；预览的内容永远不会被写进区域里。", "10%~90%，刻意保持很淡；預覽的內容永遠不會被寫進區域裡。",
                "10-90 %; bewusst blass, denn die Vorschau wird nie in die Fläche geschrieben.",
                "10-90 %; a propósito tenue, la vista previa nunca se escribe en el área.",
                "10-90 % ; volontairement pâle, l'aperçu n'est jamais écrit dans la zone.",
                "10-90%; volutamente tenue, l'anteprima non viene mai scritta nell'area.",
                "10〜90%。意図的に薄くしています。プレビューは区域に書き込まれません。",
                "10~90%. 일부러 옅게 둡니다. 미리보기는 구역에 기록되지 않습니다.",
                "10-90%; celowo blade, bo podgląd nigdy nie trafia do strefy.",
                "10-90%; de propósito suave, a prévia nunca é gravada na área.",
                "10-90%; намеренно бледно: предпросмотр никогда не записывается в зону." },
            new[] { "ToggleEnabledBinding.desc", "No key is bound by default so nothing is taken from the game; set your own here.",
                "默认不绑任何按键，免抢游戏的键位；请在这里设一个你自己的。", "預設不綁任何按鍵，免搶遊戲的鍵位；請在這裡設一個你自己的。",
                "Standardmäßig ist keine Taste belegt, damit nichts von deiner Steuerung weggenommen wird.",
                "Por defecto no hay tecla asignada, para no quitar ninguna tuya; asígnala aquí.",
                "Aucune touche n'est prise par défaut pour ne rien vous ôter ; assignez la vôtre ici.",
                "Nessun tasto assegnato di default, per non toglierti nulla; imposta il tuo qui.",
                "既定では割り当てなし。ゲームの鍵を奪わないようにするためです。ここでどうぞ。",
                "기본은 배정 없음. 게임 키를 빼앗지 않기 위함이니 여기서 직접 지정하세요.",
                "Domyślnie nic nie jest zajęte, żeby nie zabrać twojego klawisza; ustaw własny tutaj.",
                "Nenhuma tecla tomada por padrão para não tirar nada seu; defina a sua aqui.",
                "По умолчанию клавиша не занята, чтобы не отнять вашу; назначьте свою здесь." },
            new[] { "CycleModeBinding.desc", "Steps through the tracing modes above, the same thing as picking one in the dropdown.",
                "在上面那几档贴合模式之间循环，效果和直接改下拉框一样。", "在上面那幾檔貼合模式之間循環，效果和直接改下拉框一樣。",
                "Schaltet durch die obigen Modi, genauso wie über das Auswahlfeld.",
                "Recorre los modos de trazado de arriba, igual que cambiar el desplegable.",
                "Parcourt les modes de tracé ci-dessus, comme changer la liste déroulante.",
                "Scorre le modalità di tracciamento qui sopra, come cambiare il menu a tendina.",
                "上の輪郭モードを順に切り替えます。ドロップダウンで選ぶのと同じです。",
                "위 윤곽 방식을 순서대로 바꿔 줍니다. 드롭다운에서 고르는 것과 같습니다.",
                "Przechodzi przez powyższe tryby rysowania, tak jak zmiana na liście rozwijanej.",
                "Percorre os modos de traçado acima, igual a mudar a lista suspensa.",
                "Перебирает режимы обвода выше — то же самое, что выбрать в списке." },
            new[] { "RetraceNowBinding.desc", "Recomputes the areas in view along the current network, even with auto follow switched off.",
                "按当前路网重算视野内的贴合区域，自动跟随关着也能用。", "按當前路網重算視野內的貼合區域，自動跟隨關著也能用。",
                "Berechnet die sichtbaren Flächen nach dem aktuellen Netz, auch ohne Automatik.",
                "Recalcula las zonas visibles según la red actual aunque el seguimiento esté apagado.",
                "Recalcule les zones visibles selon le réseau actuel, même suivi auto coupé.",
                "Ricalcola le aree in vista sulla rete attuale anche col segui-automatico spento.",
                "自動追従がオフでも、見えている区域を今の路網に沿って作り直します。",
                "자동 추적을 꺼 두어도 화면에 보이는 구역을 현재 노선으로 다시 계산합니다.",
                "Przelicza widoczne strefy według bieżącej sieci, nawet przy wyłączonym śledzeniu.",
                "Recalcula as áreas à vista pela rede atual mesmo com o seguimento desligado.",
                "Пересчитывает видимые зоны по текущей сети, даже когда автоследование выключено." },
            new[] { "UndoFollowBinding.desc", "Restores the areas from the most recent auto-follow exactly as they were; only the last batch.",
                "把最近一次自动跟随改过的区域原样还原回去；一次只能退回最近这一批。", "把最近一次自動跟隨改過的區域原樣還原回去；一次只能退回最近這一批。",
                "Stellt die Flächen der letzten Automatik unverändert wieder her; nur der letzte Durchgang.",
                "Devuelve las zonas del último seguimiento tal como estaban; solo vale la última tanda.",
                "Restitue les zones du dernier suivi telles quelles ; seul le dernier lot revient.",
                "Ripristina le aree dell'ultima automatica esattamente com'erano; solo l'ultimo gruppo.",
                "直前の自動追従で変えた区域を元の姿に戻します。戻せるのは最後の一括分だけです。",
                "마지막 자동 추적으로 바뀐 구역을 그대로 되돌립니다. 되돌릴 수 있는 건 마지막 한 번뿐입니다.",
                "Przywraca strefy z ostatniego śledzenia dokładnie jak były; tylko ostatnia partia.",
                "Restaura as áreas da última automática como estavam; só o lote mais recente.",
                "Возвращает зоны после последнего автоследования в исходный вид; только последняя партия." },
        };

    }
}

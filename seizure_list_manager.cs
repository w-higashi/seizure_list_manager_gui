// ==============================================================================
// seizure_list_manager.cs
// 差押予定一覧 管理ツール（WPF GUI版）
//
// 【使用方法】
// build.bat を実行して seizure_list_manager.exe を生成し、ダブルクリックで起動する。
// 預金差押予定一覧.csv / 生命保険差押予定一覧.csv を閲覧・検索し、
// 差押中止案件の「引抜」操作（処理済フラグの設定）を安全に実行する。
//
// 【ビルド方法】
// build.bat を実行（.NET Framework 4.0 の csc.exe を使用）
//
// 【必要ファイル（同じフォルダに配置）】
// ＜必須＞
// - seizure_list_manager.cs  （ソースコード）
// - seizure_list_manager_config.json （設定ファイル）
// - era_mapping.json （元号マッピング）
// - seizure_list_manager.ico （アプリケーションアイコン）
// - build.bat （ビルドスクリプト）
// ==============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Xml;

// ==============================================================
// データモデル
// ==============================================================

// アプリ全体の設定
public class AppConfig
{
    public string DepositConfigPath { get; set; }    // deposit_seizure_list_config.json のパス
    public string InsuranceConfigPath { get; set; }  // insurance_seizure_list_config.json のパス
}

// CSV データ行1件（テーブルバインディング + 書き戻し用）
public class CsvRecord
{
    public string DisplayDateTime { get; set; }      // 表示用登録日時（MM/dd HH:mm）
    public string AddressNumber { get; set; }        // 宛名番号
    public string Name { get; set; }                 // 氏名
    public string Staff { get; set; }                // 処分担当（CSV上は「職員名」）
    public string DisplayExecDate { get; set; }      // 表示用執行日（令和X年Y月Z日）
    public string InstitutionName { get; set; }      // 金融機関名 or 保険会社名
    public string BranchName { get; set; }           // 支店名（生保は「—」）
    public string DocNumber { get; set; }            // 文書番号

    public int OriginalLineIndex { get; set; }       // CSVファイル上の行番号（ヘッダー行含む）
    public bool IsWithdrawn { get; set; }             // 引抜済み（フラグ1 == "2"）
    public bool IsPrinted { get; set; }               // 印刷済み（フラグ1 == "1"、WinActor 決裁用調書シナリオが設定）
    public string[] RawFields { get; set; }          // パース済み全フィールド（書き戻し用）
}

// 元号マッピング1件
public class EraEntry
{
    public string Name { get; set; }                 // 元号名（例: "令和"）
    public int StartYear { get; set; }               // 元号元年の西暦（例: 2019）
}

// ==============================================================
// JSON パーサー（手書き・外部ライブラリ不要）
// LGWAN 環境では NuGet パッケージが使えないため手動パース
// ==============================================================

public static class JsonHelper
{
    // JSON文字列から指定キーの文字列値を取得
    public static string GetString(string json, string key)
    {
        var keyIdx = json.IndexOf("\"" + key + "\"");
        if (keyIdx < 0) return null;
        var colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
        if (colonIdx < 0) return null;

        var rest = json.Substring(colonIdx + 1).TrimStart();
        if (rest.Length == 0 || rest[0] != '"') return null;

        var sb = new StringBuilder();
        bool escaped = false;
        for (int i = 1; i < rest.Length; i++)
        {
            if (escaped) { sb.Append(rest[i]); escaped = false; continue; }
            if (rest[i] == '\\') { escaped = true; continue; }
            if (rest[i] == '"') break;
            sb.Append(rest[i]);
        }
        return sb.ToString();
    }

    // JSON文字列から指定キーの整数値を取得
    public static int GetInt(string json, string key, int defaultValue = 0)
    {
        var keyIdx = json.IndexOf("\"" + key + "\"");
        if (keyIdx < 0) return defaultValue;
        var colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
        if (colonIdx < 0) return defaultValue;

        var rest = json.Substring(colonIdx + 1).TrimStart();
        var numStr = new StringBuilder();
        foreach (var c in rest)
        {
            if (char.IsDigit(c) || c == '-') numStr.Append(c);
            else if (numStr.Length > 0) break;
        }
        int result;
        return int.TryParse(numStr.ToString(), out result) ? result : defaultValue;
    }

    // JSON オブジェクト配列を取得（各要素を文字列として返す）
    public static List<string> GetObjectArray(string json, string key)
    {
        var result = new List<string>();
        var keyIdx = json.IndexOf("\"" + key + "\"");
        if (keyIdx < 0) return result;
        var arrStart = json.IndexOf('[', keyIdx);
        if (arrStart < 0) return result;
        var arrEnd = FindMatchingBracket(json, arrStart, '[', ']');
        if (arrEnd < 0) return result;

        var inner = json.Substring(arrStart + 1, arrEnd - arrStart - 1);
        int pos = 0;
        while (pos < inner.Length)
        {
            var objStart = inner.IndexOf('{', pos);
            if (objStart < 0) break;
            var objEnd = FindMatchingBracket(inner, objStart, '{', '}');
            if (objEnd < 0) break;
            result.Add(inner.Substring(objStart, objEnd - objStart + 1));
            pos = objEnd + 1;
        }
        return result;
    }

    // 対応する閉じ括弧の位置を返す
    public static int FindMatchingBracket(string json, int openIdx, char open, char close)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = openIdx; i < json.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (json[i] == '\\' && inString) { escaped = true; continue; }
            if (json[i] == '"') { inString = !inString; continue; }
            if (inString) continue;
            if (json[i] == open) depth++;
            else if (json[i] == close) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    // JSON出力用のエスケープ
    public static string Escape(string s)
    {
        if (s == null) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }
}

// ==============================================================
// ビジネスロジック
// ==============================================================

public static class BusinessLogic
{
    // CSVフィールドのエスケープ（RFC 4180準拠）
    public static string CsvEscape(string field)
    {
        if (field == null) return "";
        if (field.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        return field;
    }

    // CSV行をRFC 4180準拠でパースし、フィールドの配列として返す
    // ダブルクォートで囲まれたフィールド内のエスケープ（""→"）にも対応
    public static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
        int pos = 0;

        while (pos < line.Length)
        {
            if (line[pos] == '"')
            {
                pos++;
                var sb = new StringBuilder();
                while (pos < line.Length)
                {
                    if (line[pos] == '"')
                    {
                        if (pos + 1 < line.Length && line[pos + 1] == '"')
                        { sb.Append('"'); pos += 2; }
                        else { pos++; break; }
                    }
                    else { sb.Append(line[pos]); pos++; }
                }
                fields.Add(sb.ToString());
                if (pos < line.Length && line[pos] == ',') pos++;
            }
            else
            {
                int nextComma = line.IndexOf(',', pos);
                if (nextComma < 0) { fields.Add(line.Substring(pos)); break; }
                fields.Add(line.Substring(pos, nextComma - pos));
                pos = nextComma + 1;
            }
        }

        // 末尾がカンマで終わる場合、最後の空フィールドを追加
        // （例: "a,b,,," → 末尾の空フィールドがループで拾われない）
        if (line.Length > 0 && line[line.Length - 1] == ',')
            fields.Add("");

        return fields.ToArray();
    }

    // 7桁和暦 → DateTime 変換（era_mapping.json 使用）
    public static DateTime? WarekiToDate(string wareki, Dictionary<int, EraEntry> eraMap)
    {
        if (string.IsNullOrWhiteSpace(wareki) || wareki.Length != 7) return null;
        int eraCode, year, month, day;
        if (!int.TryParse(wareki.Substring(0, 1), out eraCode)) return null;
        if (!int.TryParse(wareki.Substring(1, 2), out year)) return null;
        if (!int.TryParse(wareki.Substring(3, 2), out month)) return null;
        if (!int.TryParse(wareki.Substring(5, 2), out day)) return null;

        EraEntry era;
        if (!eraMap.TryGetValue(eraCode, out era)) return null;
        int adYear = era.StartYear + year - 1;

        try { return new DateTime(adYear, month, day); }
        catch { return null; }
    }

    // 7桁和暦を人が読める形式に変換（例: "5080715" → "令和8年7月15日"）
    public static string FormatWarekiDisplay(string wareki, Dictionary<int, EraEntry> eraMap)
    {
        if (string.IsNullOrWhiteSpace(wareki) || wareki.Length != 7) return wareki ?? "";
        int eraCode, year, month, day;
        if (!int.TryParse(wareki.Substring(0, 1), out eraCode)) return wareki;
        if (!int.TryParse(wareki.Substring(1, 2), out year)) return wareki;
        if (!int.TryParse(wareki.Substring(3, 2), out month)) return wareki;
        if (!int.TryParse(wareki.Substring(5, 2), out day)) return wareki;

        EraEntry era;
        if (!eraMap.TryGetValue(eraCode, out era)) return wareki;
        return era.Name + year + "年" + month + "月" + day + "日";
    }

    // DateTime → 7桁和暦変換（era_mapping.json 使用）
    // 西暦年が最も近い元号を逆引き（降順で最初にマッチしたもの）
    public static string DateToWareki(DateTime date, Dictionary<int, EraEntry> eraMap)
    {
        foreach (var pair in eraMap.OrderByDescending(p => p.Value.StartYear))
        {
            if (date.Year >= pair.Value.StartYear)
            {
                int warekiYear = date.Year - pair.Value.StartYear + 1;
                return string.Format("{0}{1:D2}{2:D2}{3:D2}",
                    pair.Key, warekiYear, date.Month, date.Day);
            }
        }
        return "";
    }

    // 柔軟な日付入力パース（7桁和暦, yyyy/MM/dd, yyyyMMdd に対応）
    public static DateTime? ParseFlexibleDate(string input, Dictionary<int, EraEntry> eraMap)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        // スラッシュ区切り（yyyy/MM/dd or yyyy/M/d）
        if (input.Contains("/"))
        {
            DateTime dt;
            if (DateTime.TryParse(input, out dt)) return dt;
            return null;
        }

        // 7桁和暦
        if (input.Length == 7 && input.All(char.IsDigit))
            return WarekiToDate(input, eraMap);

        // 8桁西暦（yyyyMMdd）
        if (input.Length == 8 && input.All(char.IsDigit))
        {
            DateTime dt;
            if (DateTime.TryParseExact(input, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out dt))
                return dt;
        }

        return null;
    }

    // 登録日時を表示形式に変換（"2026/06/25 14:32:05" → "06/25 14:32"）
    public static string FormatDisplayDateTime(string rawDateTime)
    {
        if (string.IsNullOrWhiteSpace(rawDateTime)) return "";
        DateTime dt;
        if (DateTime.TryParse(rawDateTime, out dt))
            return dt.ToString("MM/dd HH:mm");
        return rawDateTime;
    }
}

// ==============================================================
// メインアプリケーション
// ==============================================================

public class SeizureListManagerApp : Application
{
    // --- 設定 ---
    private AppConfig config;
    private string exeDir;                                 // exe と同階層のフォルダパス
    private Dictionary<int, EraEntry> eraMapping;          // 元号マッピング

    // --- CSV データ ---
    private string depositCsvPath;                         // 預金CSVのフルパス（null=未設定）
    private string insuranceCsvPath;                       // 生保CSVのフルパス（null=未設定）
    private List<CsvRecord> depositRecords = new List<CsvRecord>();    // 預金データ
    private List<CsvRecord> insuranceRecords = new List<CsvRecord>();  // 生保データ

    // --- 預金/生保 CSV 列インデックス定義 ---
    // 預金 CSV（20列）
    private const int D_COL_DATETIME     = 0;
    private const int D_COL_ADDRESS_NUM  = 1;
    private const int D_COL_NAME         = 2;
    private const int D_COL_STAFF        = 3;
    private const int D_COL_EXEC_DATE    = 4;
    private const int D_COL_INSTITUTION  = 7;
    private const int D_COL_BRANCH       = 8;
    private const int D_COL_DOC_NUMBER   = 15;
    private const int D_COL_FLAG1        = 17;
    private const int D_COL_FLAG2        = 18;
    private const int D_MIN_COLUMNS      = 20;

    // 生保 CSV（21列）
    private const int I_COL_DATETIME     = 0;
    private const int I_COL_ADDRESS_NUM  = 1;
    private const int I_COL_NAME         = 2;
    private const int I_COL_STAFF        = 3;
    private const int I_COL_EXEC_DATE    = 4;
    private const int I_COL_INSTITUTION  = 7;
    private const int I_COL_DOC_NUMBER   = 16;
    private const int I_COL_FLAG1        = 18;
    private const int I_COL_FLAG2        = 19;
    private const int I_MIN_COLUMNS      = 21;

    // 編集対象列（預金・生保共通）
    private const int EDIT_COL_NAME         = 2;
    private const int EDIT_COL_STAFF        = 3;
    private const int EDIT_COL_EXEC_DATE    = 4;
    private const int EDIT_COL_RESIDENCE    = 5;
    private const int EDIT_COL_DELIVERY     = 6;

    // --- 状態 ---
    private bool isDepositTab = true;                      // 現在のアクティブタブ（true=預金）
    private string currentSortColumn = "文書番号";          // 現在のソート列
    private bool currentSortAsc = false;                   // 昇順ソートか
    private bool hideWithdrawn = false;                    // 引抜済み非表示トグル
    private bool hidePrinted = false;                      // 印刷済み非表示トグル
    private System.Windows.Threading.DispatcherTimer filterTimer;  // 即時フィルタのデバウンス用
    private CsvRecord editingRecord = null;                // 編集中のレコード（null=未編集）

    // --- キャッシュ済みブラシ ---
    // 毎回 new SolidColorBrush するとGC負荷が増えるため、
    // 静的フィールドで保持し Freeze() で描画スレッドの排他を不要にする
    private static readonly SolidColorBrush BrushAccent       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#546E7A"));
    private static readonly SolidColorBrush BrushAccentHover  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#455A64"));
    private static readonly SolidColorBrush BrushBadgeBg      = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFCCBC"));
    private static readonly SolidColorBrush BrushBadgeText    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#BF360C"));
    private static readonly SolidColorBrush BrushWithdrawnBg  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FAFAFA"));
    private static readonly SolidColorBrush BrushWithdrawnFg  = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#B0B0B0"));
    private static readonly SolidColorBrush BrushError        = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F"));
    private static readonly SolidColorBrush BrushFooter       = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#666"));
    private static readonly SolidColorBrush BrushHeaderText   = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#777"));
    private static readonly SolidColorBrush BrushBorderNormal = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D0D0D0"));
    private static readonly SolidColorBrush BrushOverlayBg    = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#777"));

    // --- 定数 ---
    private const int CSV_WRITE_MAX_RETRY = 5;             // CSV書き込みリトライ回数
    private const int CSV_WRITE_RETRY_INTERVAL_MS = 500;   // CSV書き込みリトライ間隔
    private const string DEPOSIT_CSV_FILENAME = "預金差押予定一覧.csv";
    private const string INSURANCE_CSV_FILENAME = "生命保険差押予定一覧.csv";
    private const string CONFIG_FILE = "seizure_list_manager_config.json";
    private const string ERA_MAPPING_FILE = "era_mapping.json";

    // --- UI要素 ---
    private Window window;
    private Border tabDeposit, tabInsurance;
    private TextBlock tabDepositText, tabInsuranceText;
    private TextBox searchBox;
    private ComboBox columnCombo;
    private CheckBox toggleWithdrawn;
    private CheckBox togglePrinted;
    private ListView dataTable;
    private Button btnWithdraw, btnEdit, btnReload, btnClear, confirmCancel;
    private TextBlock statusLeft, statusRight;
    private Grid confirmOverlay;
    private TextBlock confirmMessage, confirmSub;
    private TextBlock confirmPrintWarning;                  // 引抜確認の印刷済み警告

    // --- 編集オーバーレイ UI要素 ---
    private Grid editOverlay;
    private TextBox editName, editStaff, editExecDate, editResidence, editDelivery;
    private TextBox editAddrNum, editInstitution;
    private TextBlock editError;
    private Button editSave;
    private CheckBox editChkDelivery;
    private TextBlock editDeliveryError;
    private TextBlock editPrintNotice;                      // 編集オーバーレイの印刷済み注意書き
    private Popup editCalendarPopup;
    private System.Windows.Controls.Calendar editCalendar;

    // ==============================================================
    // エントリポイント
    // ==============================================================

    [STAThread]
    public static void Main(string[] args)
    {
        // ブラシを Freeze して描画パフォーマンスを向上
        BrushAccent.Freeze();
        BrushAccentHover.Freeze();
        BrushBadgeBg.Freeze();
        BrushBadgeText.Freeze();
        BrushWithdrawnBg.Freeze();
        BrushWithdrawnFg.Freeze();
        BrushError.Freeze();
        BrushFooter.Freeze();
        BrushHeaderText.Freeze();
        BrushBorderNormal.Freeze();
        BrushOverlayBg.Freeze();

        var app = new SeizureListManagerApp();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        exeDir = AppDomain.CurrentDomain.BaseDirectory;

        // --- 設定ファイル読込 ---
        var configPath = System.IO.Path.Combine(exeDir, CONFIG_FILE);
        if (!File.Exists(configPath))
        {
            MessageBox.Show("設定ファイルが見つかりません。\n\n" + configPath,
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }
        config = LoadConfig(configPath);

        // --- 元号マッピング読込 ---
        eraMapping = LoadEraMapping(System.IO.Path.Combine(exeDir, ERA_MAPPING_FILE));
        if (eraMapping.Count == 0)
        {
            MessageBox.Show("era_mapping.json が見つからないか空です。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }

        // --- CSV パス解決 ---
        depositCsvPath = ResolveCsvPath(config.DepositConfigPath, DEPOSIT_CSV_FILENAME);
        insuranceCsvPath = ResolveCsvPath(config.InsuranceConfigPath, INSURANCE_CSV_FILENAME);

        if (depositCsvPath == null && insuranceCsvPath == null)
        {
            MessageBox.Show("預金・生保いずれの CSV ファイルも見つかりませんでした。\n設定ファイルを確認してください。",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1); return;
        }

        // --- CSV 読み込み ---
        if (depositCsvPath != null)
            depositRecords = LoadCsvRecords(depositCsvPath, true);
        if (insuranceCsvPath != null)
            insuranceRecords = LoadCsvRecords(insuranceCsvPath, false);

        // --- 初期タブの決定 ---
        isDepositTab = depositCsvPath != null;

        // --- ウィンドウ構築 ---
        window = BuildWindow();
        FindControls();
        SetupEvents();
        InitializeUI();
        window.Show();
    }

    // ==============================================================
    // 設定ファイル読込
    // ==============================================================

    // seizure_list_manager_config.json を読み込む
    private AppConfig LoadConfig(string path)
    {
        var cfg = new AppConfig();
        var json = File.ReadAllText(path, Encoding.UTF8);

        var depPath = JsonHelper.GetString(json, "depositConfig");
        if (depPath != null) cfg.DepositConfigPath = depPath.Replace("\\\\", "\\");

        var insPath = JsonHelper.GetString(json, "insuranceConfig");
        if (insPath != null) cfg.InsuranceConfigPath = insPath.Replace("\\\\", "\\");

        return cfg;
    }

    // 元号マッピングを読み込む
    // キーが "3", "4", "5" 等の数値文字列であるオブジェクトを解析
    private Dictionary<int, EraEntry> LoadEraMapping(string path)
    {
        var map = new Dictionary<int, EraEntry>();
        if (!File.Exists(path)) return map;

        var json = File.ReadAllText(path, Encoding.UTF8);
        for (int code = 1; code <= 9; code++)
        {
            var key = code.ToString();
            var keyIdx = json.IndexOf("\"" + key + "\"");
            if (keyIdx < 0) continue;
            var objStart = json.IndexOf('{', keyIdx);
            if (objStart < 0) continue;
            var objEnd = JsonHelper.FindMatchingBracket(json, objStart, '{', '}');
            if (objEnd < 0) continue;
            var objJson = json.Substring(objStart, objEnd - objStart + 1);

            var name = JsonHelper.GetString(objJson, "name");
            var startYear = JsonHelper.GetInt(objJson, "startYear");
            if (name != null && startYear > 0)
                map[code] = new EraEntry { Name = name, StartYear = startYear };
        }
        return map;
    }

    // 参照先 config から outputFolder を取得し、CSV パスを解決する
    // 参照先 config が存在しない場合や CSV ファイルが存在しない場合は null を返す
    private string ResolveCsvPath(string refConfigPath, string csvFileName)
    {
        if (string.IsNullOrEmpty(refConfigPath) || !File.Exists(refConfigPath))
            return null;

        try
        {
            var json = File.ReadAllText(refConfigPath, Encoding.UTF8);
            var profiles = JsonHelper.GetObjectArray(json, "profiles");
            if (profiles.Count == 0) return null;

            var outputFolder = JsonHelper.GetString(profiles[0], "outputFolder");
            if (string.IsNullOrEmpty(outputFolder)) return null;
            outputFolder = outputFolder.Replace("\\\\", "\\");

            var csvPath = System.IO.Path.Combine(outputFolder, csvFileName);
            return File.Exists(csvPath) ? csvPath : null;
        }
        catch { return null; }
    }

    // ==============================================================
    // CSV 読み込み
    // ==============================================================

    // CSV ファイルを読み込み、CsvRecord リストとして返す
    private List<CsvRecord> LoadCsvRecords(string csvPath, bool isDeposit)
    {
        var records = new List<CsvRecord>();

        // .bak 残存チェック（前回の書き込みが未完了の場合は破損を診断し、復旧を案内する）
        CheckAndOfferRecovery(csvPath, isDeposit ? D_MIN_COLUMNS : I_MIN_COLUMNS);

        if (!File.Exists(csvPath)) return records;

        string[] lines;
        try { lines = File.ReadAllLines(csvPath, Encoding.UTF8); }
        catch (IOException)
        {
            MessageBox.Show("CSV ファイルがロックされています。\nしばらくしてから再読み込みしてください。\n\n" + csvPath,
                "読み込みエラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return records;
        }

        // 列インデックスの選択
        int colDateTime    = isDeposit ? D_COL_DATETIME    : I_COL_DATETIME;
        int colAddressNum  = isDeposit ? D_COL_ADDRESS_NUM : I_COL_ADDRESS_NUM;
        int colName        = isDeposit ? D_COL_NAME        : I_COL_NAME;
        int colStaff       = isDeposit ? D_COL_STAFF       : I_COL_STAFF;
        int colExecDate    = isDeposit ? D_COL_EXEC_DATE   : I_COL_EXEC_DATE;
        int colInstitution = isDeposit ? D_COL_INSTITUTION : I_COL_INSTITUTION;
        int colDocNumber   = isDeposit ? D_COL_DOC_NUMBER  : I_COL_DOC_NUMBER;
        int colFlag1       = isDeposit ? D_COL_FLAG1       : I_COL_FLAG1;
        int minColumns     = isDeposit ? D_MIN_COLUMNS     : I_MIN_COLUMNS;

        int skipCount = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = BusinessLogic.ParseCsvLine(lines[i]);
            if (fields.Length < minColumns) { skipCount++; continue; }

            records.Add(new CsvRecord
            {
                DisplayDateTime = BusinessLogic.FormatDisplayDateTime(fields[colDateTime]),
                AddressNumber   = fields[colAddressNum].Trim(),
                Name            = fields[colName].Trim(),
                Staff           = fields[colStaff].Trim(),
                DisplayExecDate = BusinessLogic.FormatWarekiDisplay(fields[colExecDate].Trim(), eraMapping),
                InstitutionName = fields[colInstitution].Trim(),
                BranchName      = isDeposit ? fields[D_COL_BRANCH].Trim() : "—",
                DocNumber       = fields[colDocNumber].Trim(),
                OriginalLineIndex = i,
                IsWithdrawn     = fields[colFlag1].Trim() == "2",
                IsPrinted       = fields[colFlag1].Trim() == "1",
                RawFields       = fields
            });
        }

        if (skipCount > 0)
            MessageBox.Show("CSV の " + skipCount + " 行で列数不足のためスキップしました。\n\n" + csvPath,
                "読み込み警告", MessageBoxButton.OK, MessageBoxImage.Warning);

        return records;
    }

    // ==============================================================
    // CSV 書き込み（バックアップ先行 + アトミック方式）
    // ==============================================================

    // 指定行の処理済フラグ1・2を「2」に設定する
    private bool WriteWithdrawalFlags(string csvPath, List<int> targetLineIndices, bool isDeposit)
    {
        int colFlag1 = isDeposit ? D_COL_FLAG1 : I_COL_FLAG1;
        int colFlag2 = isDeposit ? D_COL_FLAG2 : I_COL_FLAG2;

        return WriteCsvWithBackup(csvPath, delegate(string[] lines)
        {
            // 対象行のフラグ書き換え（対象行のみ再構築、他の行はそのまま保持）
            foreach (var lineIdx in targetLineIndices)
            {
                if (lineIdx >= lines.Length) continue;

                var fields = BusinessLogic.ParseCsvLine(lines[lineIdx]);
                if (fields.Length > colFlag1) fields[colFlag1] = "2";
                if (fields.Length > colFlag2) fields[colFlag2] = "2";

                lines[lineIdx] = string.Join(",",
                    fields.Select(f => BusinessLogic.CsvEscape(f)));
            }
        });
    }

    // CSV 全体書き換えの共通コア（引抜操作・編集操作で共用）
    // FileShare.None でロックしたまま「読み込み → .bak 書き出し → 書き換え → 書き戻し」を実行する。
    // 書き戻し前に元の内容を .bak に保存するため、書き戻し中のクラッシュ・ネットワーク断で
    // 本体が破損しても .bak から復旧できる（残存した .bak は CheckAndOfferRecovery が検出する）。
    private bool WriteCsvWithBackup(string csvPath, Action<string[]> modifyLines)
    {
        string bakPath = csvPath + ".bak";

        // 本体への書き戻しを開始したかどうか。開始後は外側のリトライ（再読み込み）を禁止する。
        // 書き戻し中の失敗後に切り詰められた本体を読み直すと、健全な .bak を上書きしてしまうため
        bool writeStarted = false;

        for (int retry = 1; retry <= CSV_WRITE_MAX_RETRY; retry++)
        {
            try
            {
                using (var fs = new FileStream(csvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    // 1. 全行読み込み
                    string[] lines;
                    using (var reader = new StreamReader(fs, Encoding.UTF8, true, 4096, true))
                    {
                        var allText = reader.ReadToEnd();
                        lines = allText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
                    }

                    // 2. 元の内容を .bak に書き出し（本体破損時の復旧元）
                    //    失敗時は例外で外側の catch に抜け、本体には手を付けずにリトライする
                    using (var bakStream = new FileStream(bakPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        WriteLinesToStream(bakStream, lines);
                    }

                    // 3. メモリ上で対象行を書き換え
                    modifyLines(lines);

                    // 4. 本体へ書き戻し
                    //    途中失敗時はファイルを読み直さず、メモリ上の行データで書き込みのみ再試行する
                    //    （切り詰められた本体を読み直して .bak を上書きする事故を防ぐ）
                    writeStarted = true;
                    bool written = false;
                    for (int writeRetry = 1; writeRetry <= CSV_WRITE_MAX_RETRY; writeRetry++)
                    {
                        try
                        {
                            fs.Seek(0, SeekOrigin.Begin);
                            fs.SetLength(0);
                            WriteLinesToStream(fs, lines);
                            written = true;
                            break;
                        }
                        catch
                        {
                            if (writeRetry < CSV_WRITE_MAX_RETRY)
                                System.Threading.Thread.Sleep(CSV_WRITE_RETRY_INTERVAL_MS);
                        }
                    }

                    // 書き戻し失敗: .bak を残したまま終了（次回読み込み時の復旧に使う）
                    if (!written) return false;
                }

                // 5. 書き込み成功 → .bak を削除（.bak の残存 = 書き込み未完了のマーカー）
                try { File.Delete(bakPath); } catch { }
                return true;
            }
            catch
            {
                // 書き戻し開始後に例外がここへ抜けた場合（Dispose 時の Flush 失敗等）は
                // 再読み込みせずに終了し、.bak を保護する
                if (writeStarted) return false;

                // ロック競合など書き戻し開始前の失敗 → 再オープンからやり直し
                if (retry < CSV_WRITE_MAX_RETRY)
                    System.Threading.Thread.Sleep(CSV_WRITE_RETRY_INTERVAL_MS);
            }
        }
        return false;
    }

    // 行配列を BOM 付き UTF-8 でストリームに書き出す（末尾の空行は書き出さない）
    // ストリームは閉じない（呼び出し元の using で管理する）
    private static void WriteLinesToStream(Stream stream, string[] lines)
    {
        using (var writer = new StreamWriter(stream, new UTF8Encoding(true), 4096, true))
        {
            for (int i = 0; i < lines.Length; i++)
            {
                if (i == lines.Length - 1 && string.IsNullOrEmpty(lines[i]))
                    break;  // 末尾の空行は書き出さない
                writer.WriteLine(lines[i]);
            }
            writer.Flush();
        }
    }

    // ==============================================================
    // CSV 復旧（.bak 残存時の破損診断と復旧提案）
    // ==============================================================

    // .bak が残存している場合、本体 CSV の破損を診断し、破損時のみ復旧ダイアログを表示する
    // 本体が健全な場合は .bak を削除するだけで何も表示しない
    // （.bak の残存 = 前回の WriteCsvWithBackup が書き戻しを完了できなかったことを示す）
    private void CheckAndOfferRecovery(string csvPath, int minColumns)
    {
        string bakPath = csvPath + ".bak";
        if (!File.Exists(bakPath)) return;

        // .bak を読み込み（読めない場合は診断不能のため今回は見送る）
        string[] bakLines;
        try { bakLines = File.ReadAllLines(bakPath, Encoding.UTF8); }
        catch { return; }

        // 本体を読み込み（存在しない場合は「破損」扱い、ロック中は今回は見送る）
        string[] mainLines = null;
        bool mainReadable = false;
        if (File.Exists(csvPath))
        {
            try { mainLines = File.ReadAllLines(csvPath, Encoding.UTF8); mainReadable = true; }
            catch (IOException) { return; }
        }

        bool bakHealthy = IsCsvHealthy(bakLines, minColumns);

        // 本体の破損診断:
        // SetLength(0) 後の書き戻しは先頭から順次進むため、破損は必ず
        // 「ファイル消失 / ヘッダー欠落 / 最終行の途切れ / 行数の減少」のいずれかの形をとる
        bool mainCorrupt;
        if (!mainReadable)
        {
            mainCorrupt = true;
        }
        else
        {
            mainCorrupt = !IsCsvHealthy(mainLines, minColumns);
            if (!mainCorrupt && bakHealthy)
            {
                if (mainLines[0] != bakLines[0])
                    mainCorrupt = true;  // ヘッダー不一致（ヘッダー途中で切れた痕跡）
                else if (CountDataLines(mainLines) < CountDataLines(bakLines))
                    mainCorrupt = true;  // 行数減少（書き込みは行数不変・他ツールは追記のみのため正常時は減らない）
            }
        }

        // 本体が健全 → .bak は不要（書き戻し前のクラッシュ等）。黙って削除する
        if (!mainCorrupt)
        {
            try { File.Delete(bakPath); } catch { }
            return;
        }

        // 本体破損 + .bak も不健全 → 自動復旧は提案せず、警告のみ（両ファイルは保持）
        if (!bakHealthy)
        {
            MessageBox.Show(
                "CSV ファイルが破損している可能性がありますが、\nバックアップ（.bak）も不完全なため自動復旧できません。\n\n" +
                "両方のファイルを保持しています。管理者に確認してください。\n\n" + csvPath,
                "CSV 破損の警告", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 本体破損 + .bak 健全 → 復旧ダイアログを表示する
        int mainCount = mainReadable ? CountDataLines(mainLines) : 0;
        int bakCount = CountDataLines(bakLines);
        string mainTime = mainReadable
            ? File.GetLastWriteTime(csvPath).ToString("yyyy/MM/dd HH:mm") : "—";
        string bakTime = File.GetLastWriteTime(bakPath).ToString("yyyy/MM/dd HH:mm");
        string fileName = System.IO.Path.GetFileName(csvPath);

        var result = MessageBox.Show(
            "前回の書き込みが正常に完了しなかった可能性があります。\n\n" +
            "対象: " + fileName + "\n" +
            "現在のファイル: " + mainCount + " 行（最終更新 " + mainTime + "）\n" +
            "バックアップ: " + bakCount + " 行（最終更新 " + bakTime + "）\n\n" +
            "バックアップから復旧しますか？\n\n" +
            "はい: バックアップから復旧する\n" +
            "いいえ: 現在のファイルをこのまま使う（バックアップを削除）\n" +
            "キャンセル: 今は何もしない（両方のファイルを保持）",
            "CSV 復旧の確認", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                // コピー方式: コピー中にクラッシュしても .bak が無傷で残り、次回起動時に再試行できる
                File.Copy(bakPath, csvPath, true);
                File.Delete(bakPath);
                MessageBox.Show("バックアップから復旧しました。", "CSV 復旧",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("復旧に失敗しました。バックアップは保持されます。\n\n" + ex.Message,
                    "復旧エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        else if (result == MessageBoxResult.No)
        {
            try { File.Delete(bakPath); } catch { }
        }
        // キャンセル: 何もしない（.bak が残るため、次回の読み込み時に再度確認する）
    }

    // CSV の行配列が健全か判定する（ヘッダーが存在し、最終データ行が途中で切れていないこと）
    private static bool IsCsvHealthy(string[] lines, int minColumns)
    {
        if (lines == null || lines.Length == 0) return false;
        if (string.IsNullOrWhiteSpace(lines[0])) return false;  // ヘッダー欠落

        // 最終非空行を末尾から探し、列数が揃っているか確認する
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            if (i == 0) return true;  // ヘッダーのみ（データ0行）は健全
            return BusinessLogic.ParseCsvLine(lines[i]).Length >= minColumns;
        }
        return false;
    }

    // ヘッダーを除くデータ行数を数える（空行はカウントしない）
    private static int CountDataLines(string[] lines)
    {
        int count = 0;
        for (int i = 1; i < lines.Length; i++)
            if (!string.IsNullOrWhiteSpace(lines[i])) count++;
        return count;
    }

    // ==============================================================
    // ウィンドウ構築（XAML インライン定義）
    // ==============================================================

    // ウィンドウの XAML をインライン文字列で定義し、XamlReader.Load で読み込む
    // .NET Framework 4.0 の csc.exe では XAML ファイルの埋め込み（BAML）が使えないため
    private Window BuildWindow()
    {
        string xaml = @"
<Window
    xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
    xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
    Title='差押予定一覧 管理ツール'
    Width='1000' Height='700' MinWidth='900' MinHeight='520'
    WindowStartupLocation='CenterScreen'
    Background='#F9F9F9' FontFamily='Meiryo UI'
    UseLayoutRounding='True'
    SnapsToDevicePixels='True'
    TextOptions.TextFormattingMode='Display'
    TextOptions.TextRenderingMode='ClearType'>

    <Window.Resources>
        <!-- アクセントボタン（プライマリ: スレート背景） -->
        <Style x:Key='AB' TargetType='Button'>
            <Setter Property='Background' Value='#546E7A'/><Setter Property='Foreground' Value='White'/>
            <Setter Property='FontSize' Value='12'/><Setter Property='Padding' Value='16,8'/>
            <Setter Property='Cursor' Value='Hand'/><Setter Property='BorderThickness' Value='0'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='Button'>
                    <Border x:Name='bd' Background='{TemplateBinding Background}'
                            CornerRadius='4' Padding='{TemplateBinding Padding}'>
                        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'>
                            <Setter TargetName='bd' Property='Background' Value='#455A64'/></Trigger>
                        <Trigger Property='IsEnabled' Value='False'>
                            <Setter TargetName='bd' Property='Background' Value='#CCC'/>
                            <Setter Property='Foreground' Value='#999'/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>

        <!-- ゴーストボタン（セカンダリ: 白背景＋ボーダー） -->
        <Style x:Key='GB' TargetType='Button'>
            <Setter Property='Background' Value='White'/><Setter Property='Foreground' Value='#555'/>
            <Setter Property='FontSize' Value='12'/><Setter Property='Padding' Value='16,8'/>
            <Setter Property='Cursor' Value='Hand'/><Setter Property='BorderBrush' Value='#D0D0D0'/><Setter Property='BorderThickness' Value='1'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='Button'>
                    <Border x:Name='bd' Background='{TemplateBinding Background}'
                            BorderBrush='{TemplateBinding BorderBrush}'
                            BorderThickness='{TemplateBinding BorderThickness}'
                            CornerRadius='4' Padding='{TemplateBinding Padding}'>
                        <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property='IsMouseOver' Value='True'>
                            <Setter TargetName='bd' Property='Background' Value='#ECF0F2'/></Trigger>
                        <Trigger Property='IsEnabled' Value='False'>
                            <Setter TargetName='bd' Property='Background' Value='#F5F5F5'/>
                            <Setter Property='Foreground' Value='#CCC'/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>

        <!-- GridViewColumnHeader: フラットデザイン（中央揃え） -->
        <Style TargetType='GridViewColumnHeader'>
            <Setter Property='Background' Value='#F5F7FA'/>
            <Setter Property='Foreground' Value='#777'/>
            <Setter Property='FontSize' Value='11'/>
            <Setter Property='Padding' Value='8,7'/>
            <Setter Property='HorizontalContentAlignment' Value='Center'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='GridViewColumnHeader'>
                    <Border Background='{TemplateBinding Background}'
                            BorderBrush='#E8E8E8' BorderThickness='0,0,0,1'
                            Padding='{TemplateBinding Padding}'>
                        <ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}'/>
                    </Border>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>

        <!-- ListViewItem: アクセントバー + ホバー + 選択 + 引抜済み -->
        <Style x:Key='RowItem' TargetType='ListViewItem'>
            <Setter Property='HorizontalContentAlignment' Value='Stretch'/>
            <Setter Property='Padding' Value='0'/><Setter Property='Margin' Value='0'/>
            <Setter Property='BorderThickness' Value='0'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='ListViewItem'>
                    <Grid>
                        <Border x:Name='rowBd' Background='White'
                                BorderBrush='#F0F0F0' BorderThickness='0,0,0,1'/>
                        <Border x:Name='accent' Width='3' HorizontalAlignment='Left'
                                Background='Transparent'/>
                        <GridViewRowPresenter Margin='0,7,0,7'
                            VerticalAlignment='{TemplateBinding VerticalContentAlignment}'/>
                    </Grid>
                    <ControlTemplate.Triggers>
                        <DataTrigger Binding='{Binding IsWithdrawn}' Value='True'>
                            <Setter TargetName='rowBd' Property='Background' Value='#FAFAFA'/>
                            <Setter Property='Foreground' Value='#B0B0B0'/></DataTrigger>
                        <Trigger Property='IsMouseOver' Value='True'>
                            <Setter TargetName='rowBd' Property='Background' Value='#F0F3F5'/></Trigger>
                        <Trigger Property='IsSelected' Value='True'>
                            <Setter TargetName='rowBd' Property='Background' Value='#E8EEF0'/>
                            <Setter TargetName='accent' Property='Background' Value='#546E7A'/></Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>

        <!-- TextBox: 編集オーバーレイ用（角丸 + Padding伝播、deposit_seizure_list と統一） -->
        <Style x:Key='ET' TargetType='TextBox'>
            <Setter Property='Foreground' Value='#333'/>
            <Setter Property='Template'><Setter.Value>
                <ControlTemplate TargetType='TextBox'>
                    <Border Background='{TemplateBinding Background}'
                            BorderBrush='{TemplateBinding BorderBrush}'
                            BorderThickness='{TemplateBinding BorderThickness}'
                            CornerRadius='4' Padding='{TemplateBinding Padding}'
                            SnapsToDevicePixels='True'>
                        <ScrollViewer x:Name='PART_ContentHost' Focusable='False'/></Border>
                </ControlTemplate>
            </Setter.Value></Setter>
        </Style>
    </Window.Resources>

    <DockPanel>
        <!-- ヘッダーバー -->
        <Border DockPanel.Dock='Top' Background='#546E7A' Padding='18,10'>
            <TextBlock Text='差押予定一覧 管理ツール' Foreground='White'
                       FontSize='13' FontWeight='Medium'/>
        </Border>

        <!-- フッターバー -->
        <Border DockPanel.Dock='Bottom' Background='#F0F0F0'
                BorderBrush='#E0E0E0' BorderThickness='0,1,0,0'
                Padding='18,4'>
            <DockPanel>
                <TextBlock x:Name='StatusRight' DockPanel.Dock='Right'
                           FontSize='11' Foreground='#666'/>
                <TextBlock x:Name='StatusLeft' FontSize='11' Foreground='#666'/>
            </DockPanel>
        </Border>

        <!-- メインエリア + 確認オーバーレイ -->
        <Grid>
        <DockPanel>

        <!-- タブバー -->
        <Border DockPanel.Dock='Top' Background='#FAFAFA'
                BorderBrush='#E8E8E8' BorderThickness='0,0,0,1'>
                <!-- タブ -->
                <UniformGrid Rows='1' HorizontalAlignment='Left'>
                    <Border x:Name='TabDeposit' Padding='12,8,12,6' Cursor='Hand'
                            Background='Transparent'
                            BorderThickness='0,0,0,2' BorderBrush='#546E7A'>
                        <TextBlock x:Name='TabDepositText' Text='預金'
                                   FontSize='11' HorizontalAlignment='Center'/>
                    </Border>
                    <Border x:Name='TabInsurance' Padding='12,8,12,6' Cursor='Hand'
                            Background='Transparent'
                            BorderThickness='0,0,0,2' BorderBrush='Transparent'>
                        <TextBlock x:Name='TabInsuranceText' Text='生命保険'
                                   FontSize='11' HorizontalAlignment='Center'/>
                    </Border>
                </UniformGrid>
        </Border>

        <!-- ツールバー -->
        <Border DockPanel.Dock='Top' Background='#FAFAFA'
                BorderBrush='#E8E8E8' BorderThickness='0,0,0,1'
                Padding='14,8'>
            <DockPanel>
                <!-- 再読み込みボタン（右端） -->
                <Button x:Name='ReloadButton' DockPanel.Dock='Right'
                        Style='{StaticResource GB}' Margin='10,0,0,0'
                        Padding='8,4' FontSize='11'>
                    <TextBlock Text='&#x1F504; 再読み込み' Margin='0,-1,0,1'/></Button>
                <!-- 検索ボックス + 列指定ドロップダウン -->
                <Grid Width='400' HorizontalAlignment='Left'>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width='*'/>
                        <ColumnDefinition Width='Auto'/>
                    </Grid.ColumnDefinitions>
                    <Border Grid.Column='0' Background='White'
                            BorderBrush='#D0D0D0' BorderThickness='1,1,0,1'
                            CornerRadius='4,0,0,4'>
                        <Grid>
                            <TextBlock x:Name='SearchPlaceholder' Text='検索...'
                                       FontSize='12' Foreground='#999'
                                       VerticalAlignment='Center'
                                       Margin='32,0,0,0' IsHitTestVisible='False'/>
                            <Canvas Width='14' Height='14' VerticalAlignment='Center'
                                    HorizontalAlignment='Left'
                                    Margin='10,0,0,0' IsHitTestVisible='False'>
                                <Ellipse Canvas.Left='1' Canvas.Top='1' Width='8' Height='8'
                                         Stroke='#999' StrokeThickness='1.5' Fill='Transparent'/>
                                <Line X1='8' Y1='8' X2='12' Y2='12'
                                      Stroke='#999' StrokeThickness='1.5'/>
                            </Canvas>
                            <TextBox x:Name='SearchBox' FontSize='12'
                                     Padding='28,8,28,8' BorderThickness='0'
                                     Background='Transparent'
                                     VerticalContentAlignment='Center'/>
                            <Button x:Name='ClearButton' HorizontalAlignment='Right'
                                    VerticalAlignment='Center' Margin='0,0,6,0'
                                    Visibility='Collapsed' Cursor='Hand'
                                    Focusable='False'>
                                <Button.Template>
                                    <ControlTemplate TargetType='Button'>
                                        <Border x:Name='bg' Width='20' Height='20'
                                                CornerRadius='10' Background='#E0E0E0'>
                                            <Canvas Width='10' Height='10'
                                                    HorizontalAlignment='Center'
                                                    VerticalAlignment='Center'>
                                                <Line x:Name='l1' X1='1' Y1='1' X2='9' Y2='9'
                                                      Stroke='#777' StrokeThickness='1.5'
                                                      StrokeStartLineCap='Round'
                                                      StrokeEndLineCap='Round'/>
                                                <Line x:Name='l2' X1='9' Y1='1' X2='1' Y2='9'
                                                      Stroke='#777' StrokeThickness='1.5'
                                                      StrokeStartLineCap='Round'
                                                      StrokeEndLineCap='Round'/>
                                            </Canvas>
                                        </Border>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property='IsMouseOver' Value='True'>
                                                <Setter TargetName='bg' Property='Background'
                                                        Value='#CCC'/>
                                                <Setter TargetName='l1' Property='Stroke'
                                                        Value='#555'/>
                                                <Setter TargetName='l2' Property='Stroke'
                                                        Value='#555'/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Button.Template>
                            </Button>
                        </Grid>
                    </Border>
                    <Border Grid.Column='1' BorderBrush='#D0D0D0'
                            BorderThickness='1' CornerRadius='0,4,4,0'
                            Background='White'>
                        <ComboBox x:Name='ColumnCombo' FontSize='11'
                                  MinWidth='72' Padding='8,4,4,4'
                                  VerticalContentAlignment='Center'
                                  Background='Transparent'
                                  BorderThickness='0'/>
                    </Border>
                </Grid>
            </DockPanel>
        </Border>

        <!-- アクションバー -->
        <Border DockPanel.Dock='Bottom'
                BorderBrush='#E8E8E8' BorderThickness='0,1,0,0'
                Padding='14,10'>
            <DockPanel>
                <Button x:Name='WithdrawButton' DockPanel.Dock='Right'
                        Style='{StaticResource AB}' IsEnabled='False'>
                    <TextBlock Text='&#x2193; 引抜' VerticalAlignment='Center'
                               Padding='0,0,4,0'/>
                </Button>
                <Button x:Name='EditButton' DockPanel.Dock='Right'
                        Style='{StaticResource GB}' IsEnabled='False'
                        Margin='0,0,8,0'>
                    <TextBlock Text='&#x270E; 編集' VerticalAlignment='Center'/>
                </Button>
                <CheckBox x:Name='ToggleWithdrawn' DockPanel.Dock='Left'
                          Content='引抜済みを非表示' FontSize='11'
                          Foreground='#777' VerticalContentAlignment='Center'
                          Margin='0,0,16,0'/>
                <CheckBox x:Name='TogglePrinted'
                          Content='印刷済みを非表示' FontSize='11'
                          Foreground='#777' VerticalContentAlignment='Center'
                          HorizontalAlignment='Left'/>
            </DockPanel>
        </Border>

            <!-- テーブル -->
            <ListView x:Name='DataTable' Background='White'
                      BorderThickness='0'
                      ItemContainerStyle='{StaticResource RowItem}'
                      SelectionMode='Extended'>
                <ListView.View>
                    <GridView>
                        <GridViewColumn Width='42'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Content='引抜'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='済' FontSize='12' FontWeight='Medium'
                                               Foreground='#BF360C'
                                               HorizontalAlignment='Center'>
                                        <TextBlock.Style>
                                            <Style TargetType='TextBlock'>
                                                <Setter Property='Visibility' Value='Collapsed'/>
                                                <Style.Triggers>
                                                    <DataTrigger Binding='{Binding IsWithdrawn}' Value='True'>
                                                        <Setter Property='Visibility' Value='Visible'/></DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='42'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Content='印刷'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='済' FontSize='12' FontWeight='Medium'
                                               Foreground='#107C41'
                                               HorizontalAlignment='Center'>
                                        <TextBlock.Style>
                                            <Style TargetType='TextBlock'>
                                                <Setter Property='Visibility' Value='Collapsed'/>
                                                <Style.Triggers>
                                                    <DataTrigger Binding='{Binding IsPrinted}' Value='True'>
                                                        <Setter Property='Visibility' Value='Visible'/></DataTrigger>
                                                </Style.Triggers>
                                            </Style>
                                        </TextBlock.Style>
                                    </TextBlock>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='86'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='登録日時' Content='登録日時'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding DisplayDateTime}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='82'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='宛名番号' Content='宛名番号'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding AddressNumber}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch' FontFamily='Consolas'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='120'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='氏名' Content='氏名'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding Name}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch'
                                               TextTrimming='CharacterEllipsis'
                                               ToolTip='{Binding Name}'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='96'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='処分担当' Content='処分担当'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding Staff}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch'
                                               TextTrimming='CharacterEllipsis'
                                               ToolTip='{Binding Staff}'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='104'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='執行日' Content='執行日'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding DisplayExecDate}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='120'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='金融機関名' Content='金融機関名'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding InstitutionName}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch'
                                               TextTrimming='CharacterEllipsis'
                                               ToolTip='{Binding InstitutionName}'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='100'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='支店名' Content='支店名'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding BranchName}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch'
                                               TextTrimming='CharacterEllipsis'
                                               ToolTip='{Binding BranchName}'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                        <GridViewColumn Width='78'>
                            <GridViewColumn.Header>
                                <GridViewColumnHeader Tag='文書番号' Content='文書番号 ▼'/>
                            </GridViewColumn.Header>
                            <GridViewColumn.CellTemplate>
                                <DataTemplate>
                                    <TextBlock Text='{Binding DocNumber}' TextAlignment='Center'
                                               HorizontalAlignment='Stretch' FontFamily='Consolas'/>
                                </DataTemplate>
                            </GridViewColumn.CellTemplate>
                        </GridViewColumn>
                    </GridView>
                </ListView.View>
            </ListView>

        </DockPanel>

            <!-- 引抜確認オーバーレイ -->
            <Grid x:Name='ConfirmOverlay' Visibility='Collapsed' Opacity='0'
                  Background='#CCFFFFFF'>
                <Border Background='White' CornerRadius='8'
                        Padding='24,20' MaxWidth='520'
                        HorizontalAlignment='Center' VerticalAlignment='Center'>
                    <Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='4' Opacity='0.12'/></Border.Effect>
                    <StackPanel>
                        <TextBlock x:Name='ConfirmMessage' FontSize='13'
                                   TextWrapping='Wrap' Margin='0,0,0,8'/>
                        <TextBlock x:Name='ConfirmSub' FontSize='11'
                                   Foreground='#777' Margin='0,0,0,20'
                                   Text='処理対象から除外されます'/>
                        <TextBlock x:Name='ConfirmPrintWarning' FontSize='11'
                                   Foreground='#D32F2F' Margin='0,-12,0,20'
                                   TextWrapping='Wrap' Visibility='Collapsed'/>
                        <StackPanel Orientation='Horizontal'
                                    HorizontalAlignment='Right'>
                            <Button x:Name='ConfirmCancel' Content='キャンセル'
                                    Style='{StaticResource GB}' Margin='0,0,8,0'/>
                            <Button x:Name='ConfirmExecute' Content='実行'
                                    Style='{StaticResource AB}'/>
                        </StackPanel>
                    </StackPanel>
                </Border>
            </Grid>

            <!-- 編集オーバーレイ -->
            <Grid x:Name='EditOverlay' Visibility='Collapsed' Opacity='0'
                  Background='#CCFFFFFF'>
                <Border Background='White' CornerRadius='8'
                        Padding='28,24' Width='580'
                        HorizontalAlignment='Center' VerticalAlignment='Center'>
                    <Border.Effect><DropShadowEffect BlurRadius='16' ShadowDepth='4' Opacity='0.12'/></Border.Effect>
                    <StackPanel>
                        <!-- タイトル + ✕ボタン -->
                        <DockPanel Margin='0,0,0,14'>
                            <Button x:Name='EditCloseButton' DockPanel.Dock='Right'
                                    Cursor='Hand' Focusable='False'
                                    VerticalAlignment='Top' Margin='0,-4,-4,0'>
                                <Button.Template>
                                    <ControlTemplate TargetType='Button'>
                                        <Border x:Name='ecbg' Width='24' Height='24'
                                                CornerRadius='4' Background='Transparent'>
                                            <TextBlock Text='&#x2715;' FontSize='13'
                                                       Foreground='#999'
                                                       HorizontalAlignment='Center'
                                                       VerticalAlignment='Center'/>
                                        </Border>
                                        <ControlTemplate.Triggers>
                                            <Trigger Property='IsMouseOver' Value='True'>
                                                <Setter TargetName='ecbg' Property='Background' Value='#F0F0F0'/>
                                            </Trigger>
                                        </ControlTemplate.Triggers>
                                    </ControlTemplate>
                                </Button.Template>
                            </Button>
                            <TextBlock Text='&#x270E; 登録内容の編集' FontSize='13' FontWeight='Medium'/>
                        </DockPanel>
                        <TextBlock x:Name='EditPrintNotice' FontSize='11'
                                   Foreground='#D32F2F' Margin='0,-6,0,10'
                                   TextWrapping='Wrap' Visibility='Collapsed'
                                   Text='この行は印刷済みです。修正後は再印刷が必要です。'/>
                        <!-- 読取専用フィールド: 宛名番号 + 金融機関名 -->
                        <Grid Margin='0,0,0,10'>
                            <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
                            <StackPanel Grid.Column='0'>
                                <TextBlock Text='宛名番号' FontSize='11' Foreground='#777' Margin='0,0,0,3'/>
                                <TextBox x:Name='EditAddrNum' Style='{StaticResource ET}' IsReadOnly='True' Background='#F3F3F3'
                                         BorderBrush='#E8E8E8' FontSize='12' FontFamily='Consolas' Padding='5,4'/>
                            </StackPanel>
                            <StackPanel Grid.Column='2'>
                                <TextBlock Text='金融機関名' FontSize='11' Foreground='#777' Margin='0,0,0,3'/>
                                <TextBox x:Name='EditInstitution' Style='{StaticResource ET}' IsReadOnly='True' Background='#F3F3F3'
                                         BorderBrush='#E8E8E8' FontSize='12' Padding='5,4'/>
                            </StackPanel>
                        </Grid>
                        <!-- 編集フィールド: 氏名 + 処分担当 -->
                        <Grid Margin='0,0,0,10'>
                            <Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='16'/><ColumnDefinition Width='*'/></Grid.ColumnDefinitions>
                            <StackPanel Grid.Column='0'>
                                <TextBlock FontSize='11' Foreground='#777' Margin='0,0,0,3'>氏名 &#x270E;</TextBlock>
                                <TextBox x:Name='EditName' Style='{StaticResource ET}' FontSize='12' Padding='5,4' BorderBrush='#D0D0D0'/>
                            </StackPanel>
                            <StackPanel Grid.Column='2'>
                                <TextBlock FontSize='11' Foreground='#777' Margin='0,0,0,3'>処分担当 &#x270E;</TextBlock>
                                <TextBox x:Name='EditStaff' Style='{StaticResource ET}' FontSize='12' Padding='5,4' BorderBrush='#D0D0D0'/>
                            </StackPanel>
                        </Grid>
                        <!-- 住民票住所 -->
                        <StackPanel Margin='0,0,0,10'>
                            <TextBlock FontSize='11' Foreground='#777' Margin='0,0,0,3'>住民票住所 &#x270E;</TextBlock>
                            <TextBox x:Name='EditResidence' Style='{StaticResource ET}' FontSize='12' Padding='5,4' BorderBrush='#D0D0D0'/>
                        </StackPanel>
                        <!-- 届出住所 + 出力チェック -->
                        <StackPanel Margin='0,0,0,10'>
                            <TextBlock FontSize='11' Foreground='#777' Margin='0,0,0,3'>届出住所 &#x270E;</TextBlock>
                            <TextBox x:Name='EditDelivery' Style='{StaticResource ET}' FontSize='12' Padding='5,4' BorderBrush='#D0D0D0'/>
                            <TextBlock x:Name='EditDeliveryError' Foreground='#D32F2F' FontSize='10'
                                       Visibility='Collapsed' Margin='0,1,0,0'/>
                            <CheckBox x:Name='EditChkDelivery'
                                      Content='届出住所を差押通知書に出力する'
                                      FontSize='12' Margin='0,4,0,0'/>
                        </StackPanel>
                        <!-- 執行日 + カレンダーPopup -->
                        <StackPanel Margin='0,0,0,16'>
                            <TextBlock FontSize='11' Foreground='#777' Margin='0,0,0,3'>執行日 &#x270E;</TextBlock>
                            <StackPanel Orientation='Horizontal'>
                                <TextBox x:Name='EditExecDate' Style='{StaticResource ET}' FontSize='12' Padding='5,4'
                                         BorderBrush='#D0D0D0' FontFamily='Consolas' Width='120'/>
                                <Button x:Name='EditCalendarButton' Style='{StaticResource GB}'
                                        Padding='6,4' Margin='4,0,0,0'>
                                    <TextBlock Text='&#x1F4C5;' FontSize='13'/></Button>
                                <Popup x:Name='EditCalendarPopup' StaysOpen='False'
                                       Placement='Bottom' AllowsTransparency='True'>
                                    <Border Background='White' BorderBrush='#D0D0D0'
                                            BorderThickness='1' CornerRadius='6'
                                            Padding='8' Margin='0,4,0,0'>
                                        <Border.Effect><DropShadowEffect BlurRadius='12' ShadowDepth='3' Opacity='0.15'/></Border.Effect>
                                        <Calendar x:Name='EditCalendar' SelectionMode='SingleDate'/>
                                    </Border>
                                </Popup>
                            </StackPanel>
                        </StackPanel>
                        <!-- バリデーション + ボタン -->
                        <DockPanel>
                            <StackPanel DockPanel.Dock='Right' Orientation='Horizontal'>
                                <Button x:Name='EditCancelButton' Content='キャンセル'
                                        Style='{StaticResource GB}' Margin='0,0,8,0'/>
                                <Button x:Name='EditSaveButton' Content='保存'
                                        Style='{StaticResource AB}'/>
                            </StackPanel>
                            <TextBlock x:Name='EditError' FontSize='11'
                                       Foreground='#D32F2F' VerticalAlignment='Center'
                                       TextWrapping='Wrap'/>
                        </DockPanel>
                    </StackPanel>
                </Border>
            </Grid>
        </Grid>
    </DockPanel>
</Window>";

        using (var reader = new XmlTextReader(new StringReader(xaml)))
            return (Window)XamlReader.Load(reader);
    }

    // ==============================================================
    // UI 初期化
    // ==============================================================

    // BuildWindow で生成した XAML ツリーから x:Name で各要素を取得
    private void FindControls()
    {
        tabDeposit      = (Border)window.FindName("TabDeposit");
        tabInsurance    = (Border)window.FindName("TabInsurance");
        tabDepositText  = (TextBlock)window.FindName("TabDepositText");
        tabInsuranceText = (TextBlock)window.FindName("TabInsuranceText");
        searchBox       = (TextBox)window.FindName("SearchBox");
        columnCombo     = (ComboBox)window.FindName("ColumnCombo");
        toggleWithdrawn = (CheckBox)window.FindName("ToggleWithdrawn");
        togglePrinted   = (CheckBox)window.FindName("TogglePrinted");
        dataTable       = (ListView)window.FindName("DataTable");
        btnWithdraw     = (Button)window.FindName("WithdrawButton");
        btnEdit         = (Button)window.FindName("EditButton");
        btnReload       = (Button)window.FindName("ReloadButton");
        btnClear        = (Button)window.FindName("ClearButton");
        statusLeft      = (TextBlock)window.FindName("StatusLeft");
        statusRight     = (TextBlock)window.FindName("StatusRight");
        confirmOverlay  = (Grid)window.FindName("ConfirmOverlay");
        confirmMessage  = (TextBlock)window.FindName("ConfirmMessage");
        confirmSub      = (TextBlock)window.FindName("ConfirmSub");
        confirmPrintWarning = (TextBlock)window.FindName("ConfirmPrintWarning");
        confirmCancel   = (Button)window.FindName("ConfirmCancel");

        // 編集オーバーレイ
        editOverlay     = (Grid)window.FindName("EditOverlay");
        editAddrNum     = (TextBox)window.FindName("EditAddrNum");
        editInstitution = (TextBox)window.FindName("EditInstitution");
        editName        = (TextBox)window.FindName("EditName");
        editStaff       = (TextBox)window.FindName("EditStaff");
        editResidence   = (TextBox)window.FindName("EditResidence");
        editDelivery    = (TextBox)window.FindName("EditDelivery");
        editChkDelivery = (CheckBox)window.FindName("EditChkDelivery");
        editDeliveryError = (TextBlock)window.FindName("EditDeliveryError");
        editPrintNotice = (TextBlock)window.FindName("EditPrintNotice");
        editExecDate    = (TextBox)window.FindName("EditExecDate");
        editError       = (TextBlock)window.FindName("EditError");
        editSave        = (Button)window.FindName("EditSaveButton");
        editCalendarPopup = (Popup)window.FindName("EditCalendarPopup");
        editCalendar    = (System.Windows.Controls.Calendar)window.FindName("EditCalendar");
    }

    // イベントハンドラの登録
    private void SetupEvents()
    {
        // タブ切替（CSV が存在し、かつ非アクティブの場合のみ切替可能）
        tabDeposit.MouseLeftButtonDown += delegate { if (depositCsvPath != null && !isDepositTab) SwitchTab(true); };
        tabInsurance.MouseLeftButtonDown += delegate { if (insuranceCsvPath != null && isDepositTab) SwitchTab(false); };

        // 即時フィルタ（デバウンス 150ms で再描画の無駄を抑制）
        filterTimer = new System.Windows.Threading.DispatcherTimer();
        filterTimer.Interval = TimeSpan.FromMilliseconds(150);
        filterTimer.Tick += delegate { filterTimer.Stop(); PopulateTable(); };

        var placeholder = (TextBlock)window.FindName("SearchPlaceholder");
        searchBox.TextChanged += delegate
        {
            bool hasText = !string.IsNullOrEmpty(searchBox.Text);
            btnClear.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
            placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            filterTimer.Stop();
            filterTimer.Start();
        };
        columnCombo.SelectionChanged += delegate
        {
            filterTimer.Stop();
            filterTimer.Start();
        };

        // × クリアボタン
        btnClear.Click += delegate
        {
            searchBox.Text = "";
            searchBox.Focus();
        };

        // 引抜済み非表示トグル
        toggleWithdrawn.Checked += delegate { hideWithdrawn = true; PopulateTable(); };
        toggleWithdrawn.Unchecked += delegate { hideWithdrawn = false; PopulateTable(); };
        togglePrinted.Checked += delegate { hidePrinted = true; PopulateTable(); };
        togglePrinted.Unchecked += delegate { hidePrinted = false; PopulateTable(); };

        // テーブル選択変更
        dataTable.SelectionChanged += delegate { UpdateButtonState(); };

        // 列ヘッダークリックでソート
        dataTable.AddHandler(GridViewColumnHeader.ClickEvent,
            new RoutedEventHandler(OnColumnHeaderClick));

        // 引抜ボタン
        btnWithdraw.Click += delegate { ShowConfirmOverlay(); };

        // 編集ボタン
        btnEdit.Click += delegate { ShowEditOverlay(); };

        // テーブルダブルクリック → 編集オーバーレイ表示（データ行のみ）
        dataTable.MouseDoubleClick += delegate(object s, MouseButtonEventArgs me)
        {
            // VisualTree を辿り、ListViewItem 上のクリックか確認する
            DependencyObject hit = me.OriginalSource as DependencyObject;
            while (hit != null && !(hit is ListViewItem))
                hit = VisualTreeHelper.GetParent(hit);
            if (hit != null && HasEditableSelection()) ShowEditOverlay();
        };

        // 確認オーバーレイのボタン
        var confirmExecute = (Button)window.FindName("ConfirmExecute");
        confirmCancel.Click += delegate { FadeOut(confirmOverlay, delegate { dataTable.Focus(); }); };
        confirmExecute.Click += delegate { ExecuteWithdrawal(); };

        // 編集オーバーレイのボタン
        var editClose = (Button)window.FindName("EditCloseButton");
        var editCancel = (Button)window.FindName("EditCancelButton");
        editClose.Click += delegate { CloseEditOverlay(); };
        editCancel.Click += delegate { CloseEditOverlay(); };
        editSave.Click += delegate { SaveEdit(); };

        // 届出住所バリデーション
        editDelivery.TextChanged += delegate { ValidateEditDelivery(); };
        editChkDelivery.Checked += delegate { ValidateEditDelivery(); };
        editChkDelivery.Unchecked += delegate { ValidateEditDelivery(); };

        // 編集オーバーレイの執行日入力 → LostFocus で yyyy/MM/dd に正規化
        editExecDate.LostFocus += delegate
        {
            var input = editExecDate.Text.Trim();
            if (string.IsNullOrEmpty(input)) return;
            var dt = BusinessLogic.ParseFlexibleDate(input, eraMapping);
            if (dt.HasValue)
            {
                editExecDate.Text = dt.Value.ToString("yyyy/MM/dd");
                editExecDate.BorderBrush = BrushBorderNormal;
            }
            else
            {
                editExecDate.BorderBrush = BrushError;
            }
        };

        // 編集オーバーレイのカレンダーボタン
        var editCalBtn = (Button)window.FindName("EditCalendarButton");
        editCalBtn.Click += delegate
        {
            editCalendarPopup.PlacementTarget = editCalBtn;
            if (!editCalendarPopup.IsOpen)
            {
                editCalendar.DisplayMode = CalendarMode.Month;
                var dt = BusinessLogic.ParseFlexibleDate(editExecDate.Text.Trim(), eraMapping);
                editCalendar.DisplayDate = dt ?? DateTime.Today;
                editCalendar.SelectedDates.Clear();
            }
            editCalendarPopup.IsOpen = !editCalendarPopup.IsOpen;
        };
        editCalendar.SelectedDatesChanged += delegate
        {
            if (editCalendar.SelectedDate.HasValue)
            {
                editExecDate.Text = editCalendar.SelectedDate.Value.ToString("yyyy/MM/dd");
                editExecDate.BorderBrush = BrushBorderNormal;
                editCalendarPopup.IsOpen = false;
            }
        };

        // 編集オーバーレイの空白クリックでテキストボックスからフォーカスを外す
        editOverlay.MouseDown += delegate(object s, MouseButtonEventArgs me)
        {
            if (me.OriginalSource is System.Windows.Controls.Panel ||
                me.OriginalSource is Border)
            {
                FocusManager.SetFocusedElement(window, editOverlay);
                Keyboard.ClearFocus();
            }
        };

        // 再読み込みボタン
        btnReload.Click += delegate { ReloadCurrentTab(); };

        // テーブル空白エリアクリックで選択解除
        dataTable.MouseLeftButtonDown += OnTableEmptyAreaClick;

        // コンテキストメニュー
        var ctxMenu = new ContextMenu();
        var ctxEdit = new MenuItem { Header = "編集" };
        ctxEdit.Click += delegate { ShowEditOverlay(); };
        ctxMenu.Items.Add(ctxEdit);
        var ctxWithdraw = new MenuItem { Header = "引抜" };
        ctxWithdraw.Click += delegate { ShowConfirmOverlay(); };
        ctxMenu.Items.Add(ctxWithdraw);
        ctxMenu.Opened += delegate
        {
            ctxEdit.IsEnabled = HasEditableSelection();
            ctxWithdraw.IsEnabled = HasWithdrawableSelection();
        };
        dataTable.ContextMenu = ctxMenu;

        // ウィンドウリサイズ時の列幅調整
        window.SizeChanged += delegate { AdjustColumnWidths(); };

        // ショートカットキー
        window.InputBindings.Add(new KeyBinding(
            new RelayCommand(delegate { searchBox.Focus(); searchBox.SelectAll(); }),
            new KeyGesture(Key.F, ModifierKeys.Control)));
        window.InputBindings.Add(new KeyBinding(
            new RelayCommand(delegate
            {
                if (editOverlay.Visibility == Visibility.Visible) CloseEditOverlay();
            }),
            new KeyGesture(Key.Escape)));
    }

    // UI の初期状態を設定
    private void InitializeUI()
    {
        // 列指定ドロップダウンの選択肢
        columnCombo.Items.Add("宛名番号");
        columnCombo.Items.Add("氏名");
        columnCombo.Items.Add("処分担当");
        columnCombo.Items.Add("文書番号");
        columnCombo.SelectedIndex = 0;

        // タブの有効/無効設定
        if (depositCsvPath == null)
        {
            tabDepositText.Foreground = BrushBorderNormal;
            tabDeposit.IsEnabled = false;
            tabDeposit.Cursor = System.Windows.Input.Cursors.Arrow;
        }
        if (insuranceCsvPath == null)
        {
            tabInsuranceText.Foreground = BrushBorderNormal;
            tabInsurance.IsEnabled = false;
            tabInsurance.Cursor = System.Windows.Input.Cursors.Arrow;
        }

        // 初期タブの表示
        UpdateTabVisuals();
        PopulateTable();
        searchBox.Focus();
    }

    // ==============================================================
    // タブ切替
    // ==============================================================

    // タブを切り替えてデータを再表示する
    private void SwitchTab(bool toDeposit)
    {
        isDepositTab = toDeposit;
        searchBox.Text = "";
        columnCombo.SelectedIndex = 0;
        currentSortColumn = "文書番号";
        currentSortAsc = false;

        UpdateTabVisuals();
        PopulateTable();
    }

    // タブの視覚状態を更新する
    private void UpdateTabVisuals()
    {
        if (isDepositTab)
        {
            tabDepositText.Foreground = BrushAccent;
            tabDepositText.FontWeight = FontWeights.Medium;
            tabDeposit.BorderBrush = BrushAccent;
            tabInsuranceText.Foreground = BrushHeaderText;
            tabInsuranceText.FontWeight = FontWeights.Normal;
            tabInsurance.BorderBrush = Brushes.Transparent;
        }
        else
        {
            tabInsuranceText.Foreground = BrushAccent;
            tabInsuranceText.FontWeight = FontWeights.Medium;
            tabInsurance.BorderBrush = BrushAccent;
            tabDepositText.Foreground = BrushHeaderText;
            tabDepositText.FontWeight = FontWeights.Normal;
            tabDeposit.BorderBrush = Brushes.Transparent;
        }
    }

    // ==============================================================
    // テーブル表示
    // ==============================================================

    // 現在のタブに対応するデータをテーブルに表示する
    private void PopulateTable()
    {
        var records = GetCurrentRecords();
        var filtered = FilterRecords(records);
        var sorted = SortRecords(filtered);

        dataTable.Items.Clear();
        foreach (var rec in sorted)
            dataTable.Items.Add(rec);

        UpdateSortIndicators();
        AdjustColumnWidths();
        UpdateFooter();
        UpdateButtonState();
    }

    // 現在のタブに対応するレコードリストを返す
    private List<CsvRecord> GetCurrentRecords()
    {
        return isDepositTab ? depositRecords : insuranceRecords;
    }

    // ==============================================================
    // 検索フィルタ
    // ==============================================================

    // レコードリストにフィルタを適用して返す
    private List<CsvRecord> FilterRecords(List<CsvRecord> records)
    {
        var result = new List<CsvRecord>();
        string query = (searchBox.Text ?? "").Trim();
        string column = columnCombo.SelectedItem as string ?? "宛名番号";

        foreach (var rec in records)
        {
            // 引抜済み非表示
            if (hideWithdrawn && rec.IsWithdrawn) continue;

            // 印刷済み非表示
            if (hidePrinted && rec.IsPrinted) continue;

            // テキストフィルタ
            if (!string.IsNullOrEmpty(query))
            {
                string target;
                switch (column)
                {
                    case "氏名":     target = rec.Name; break;
                    case "処分担当": target = rec.Staff; break;
                    case "文書番号": target = rec.DocNumber; break;
                    default:         target = rec.AddressNumber; break;
                }
                if (target == null || target.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            result.Add(rec);
        }
        return result;
    }

    // ==============================================================
    // ソート
    // ==============================================================

    // 列ヘッダークリック時のソート処理
    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        var header = e.OriginalSource as GridViewColumnHeader;
        if (header == null || header.Tag == null) return;
        string tag = header.Tag.ToString();

        if (currentSortColumn == tag)
            currentSortAsc = !currentSortAsc;
        else
        {
            currentSortColumn = tag;
            currentSortAsc = true;
        }

        PopulateTable();
    }

    // レコードリストをソートして返す
    private List<CsvRecord> SortRecords(List<CsvRecord> records)
    {
        Func<CsvRecord, string> keySelector;
        switch (currentSortColumn)
        {
            case "宛名番号":   keySelector = r => r.AddressNumber; break;
            case "氏名":       keySelector = r => r.Name; break;
            case "処分担当":   keySelector = r => r.Staff; break;
            case "執行日":     keySelector = r => r.DisplayExecDate; break;
            case "金融機関名": keySelector = r => r.InstitutionName; break;
            case "支店名":     keySelector = r => r.BranchName; break;
            case "文書番号":   keySelector = r => r.DocNumber; break;
            default:           keySelector = r => r.DisplayDateTime; break;
        }

        return currentSortAsc
            ? records.OrderBy(keySelector).ToList()
            : records.OrderByDescending(keySelector).ToList();
    }

    // ソートインジケータ（▲/▼）を更新する
    private void UpdateSortIndicators()
    {
        var gridView = dataTable.View as GridView;
        if (gridView == null) return;

        string indicator = currentSortAsc ? " ▲" : " ▼";

        foreach (var col in gridView.Columns)
        {
            var header = col.Header as GridViewColumnHeader;
            if (header == null || header.Tag == null) continue;
            string tag = header.Tag.ToString();
            // ベーステキスト（▲▼を除去）
            string baseText = tag;
            header.Content = tag == currentSortColumn ? baseText + indicator : baseText;
        }
    }

    // ==============================================================
    // 列幅の自動調整
    // ==============================================================

    // ウィンドウリサイズ時・タブ切替時に氏名列の幅を調整する
    // 生保タブでは支店名列を非表示（Width=0）にし、金融機関名列を拡大する
    private void AdjustColumnWidths()
    {
        var gridView = dataTable.View as GridView;
        if (gridView == null || gridView.Columns.Count < 10) return;

        // 生保タブでは支店名列を非表示、金融機関名列を拡大（保険会社名が長いため）
        double branchWidth = isDepositTab ? 120 : 0;
        double institutionWidth = isDepositTab ? 130 : 200;
        gridView.Columns[7].Width = institutionWidth;  // 金融機関名列
        gridView.Columns[8].Width = branchWidth;       // 支店名列

        double totalWidth = dataTable.ActualWidth - SystemParameters.VerticalScrollBarWidth - 4;
        // 引抜(42) + 印刷(42) + 登録日時(86) + 宛名番号(82) + 処分担当(96) + 執行日(104) + 金融機関名(130/200) + 支店名(120/0) + 文書番号(78)
        double fixedWidth = 42 + 42 + 86 + 82 + 96 + 104 + institutionWidth + branchWidth + 78;
        double remaining = totalWidth - fixedWidth;
        if (remaining < 80) remaining = 80;

        gridView.Columns[4].Width = remaining;  // 氏名列（可変幅）
    }

    // ==============================================================
    // 引抜・編集操作
    // ==============================================================

    // 引抜・編集ボタンの有効/無効を更新する
    private void UpdateButtonState()
    {
        btnWithdraw.IsEnabled = HasWithdrawableSelection();
        btnEdit.IsEnabled = HasEditableSelection();
    }

    // 選択行に引抜可能な行が含まれているか
    private bool HasWithdrawableSelection()
    {
        foreach (var item in dataTable.SelectedItems)
        {
            var rec = item as CsvRecord;
            if (rec != null && !rec.IsWithdrawn) return true;
        }
        return false;
    }

    // 引抜済みでない行が1件だけ選択されているか（編集の有効条件）
    private bool HasEditableSelection()
    {
        if (dataTable.SelectedItems.Count != 1) return false;
        var rec = dataTable.SelectedItems[0] as CsvRecord;
        return rec != null && !rec.IsWithdrawn;
    }

    // 引抜確認オーバーレイを表示する
    private void ShowConfirmOverlay()
    {
        if (!HasWithdrawableSelection()) return;

        // 引抜対象の件数を算出
        var targets = new List<CsvRecord>();
        foreach (var item in dataTable.SelectedItems)
        {
            var rec = item as CsvRecord;
            if (rec != null && !rec.IsWithdrawn) targets.Add(rec);
        }

        if (targets.Count == 1)
            confirmMessage.Text = "宛名番号 " + targets[0].AddressNumber +
                "（執行日: " + targets[0].DisplayExecDate + "）の登録を引き抜きます";
        else
            confirmMessage.Text = targets.Count + " 件の登録を引き抜きます";

        // 印刷済み（フラグ1=1）の行が含まれる場合は警告を表示する
        int printedCount = targets.Count(r => r.IsPrinted);
        if (printedCount > 0)
        {
            confirmPrintWarning.Text = (targets.Count == 1)
                ? "この行は印刷済みです。引抜後、担当職員へ連絡してください。"
                : printedCount + " 件は印刷済みです。引抜後、担当職員へ連絡してください。";
            confirmPrintWarning.Visibility = Visibility.Visible;
        }
        else
        {
            confirmPrintWarning.Visibility = Visibility.Collapsed;
        }

        FadeIn(confirmOverlay);
        confirmCancel.Focus();
    }

    // 引抜を実行する
    private void ExecuteWithdrawal()
    {
        var targets = new List<CsvRecord>();
        foreach (var item in dataTable.SelectedItems)
        {
            var rec = item as CsvRecord;
            if (rec != null && !rec.IsWithdrawn) targets.Add(rec);
        }
        if (targets.Count == 0) { FadeOut(confirmOverlay); return; }

        var lineIndices = targets.Select(r => r.OriginalLineIndex).ToList();
        string csvPath = isDepositTab ? depositCsvPath : insuranceCsvPath;

        bool success = WriteWithdrawalFlags(csvPath, lineIndices, isDepositTab);

        FadeOut(confirmOverlay, delegate
        {
            if (success)
            {
                // CSV をディスクから再読み込み（他ユーザーの変更も反映）
                if (isDepositTab && depositCsvPath != null)
                    depositRecords = LoadCsvRecords(depositCsvPath, true);
                else if (!isDepositTab && insuranceCsvPath != null)
                    insuranceRecords = LoadCsvRecords(insuranceCsvPath, false);
                PopulateTable();
            }
            else
            {
                MessageBox.Show(
                    "CSV ファイルへの書き込みに失敗しました。\nファイルが他のプロセスで使用されている可能性があります。\n\n" +
                    "書き込みが途中で失敗した場合はバックアップ（.bak）が残っており、\n次回の読み込み時に復旧を確認します。",
                    "書き込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            // ボタンからフォーカスを外し、Enter 連打での再発火を防止
            dataTable.Focus();
        });
    }

    // ==============================================================
    // 編集操作
    // ==============================================================

    // 編集オーバーレイを表示し、選択行のデータをフォームに反映する
    private void ShowEditOverlay()
    {
        if (!HasEditableSelection()) return;

        editingRecord = dataTable.SelectedItems[0] as CsvRecord;
        if (editingRecord == null) return;

        // 読取専用フィールド
        editAddrNum.Text = editingRecord.AddressNumber;
        editInstitution.Text = editingRecord.InstitutionName;

        // 編集対象フィールド（CSV の生値を表示）
        var fields = editingRecord.RawFields;
        editName.Text = fields.Length > EDIT_COL_NAME ? fields[EDIT_COL_NAME].Trim() : "";
        editStaff.Text = fields.Length > EDIT_COL_STAFF ? fields[EDIT_COL_STAFF].Trim() : "";
        editResidence.Text = fields.Length > EDIT_COL_RESIDENCE ? fields[EDIT_COL_RESIDENCE].Trim() : "";

        // 届出住所: CSV の「（届出：...）」ラッパーを解析してチェック状態を判定
        var rawDelivery = fields.Length > EDIT_COL_DELIVERY ? fields[EDIT_COL_DELIVERY].Trim() : "";
        if (rawDelivery.StartsWith("（届出：") && rawDelivery.EndsWith("）"))
        {
            editDelivery.Text = rawDelivery.Substring(4, rawDelivery.Length - 5);
            editChkDelivery.IsChecked = true;
        }
        else
        {
            editDelivery.Text = rawDelivery;
            editChkDelivery.IsChecked = !string.IsNullOrEmpty(rawDelivery);
        }
        editDeliveryError.Visibility = Visibility.Collapsed;

        // 印刷済み（フラグ1=1）の行には注意書きを表示する
        editPrintNotice.Visibility = editingRecord.IsPrinted
            ? Visibility.Visible : Visibility.Collapsed;

        // 執行日: 7桁和暦 → yyyy/MM/dd に変換して表示
        var rawExec = fields.Length > EDIT_COL_EXEC_DATE ? fields[EDIT_COL_EXEC_DATE].Trim() : "";
        var execDt = BusinessLogic.ParseFlexibleDate(rawExec, eraMapping);
        editExecDate.Text = execDt.HasValue ? execDt.Value.ToString("yyyy/MM/dd") : rawExec;

        // バリデーション状態のリセット
        editError.Text = "";
        editName.BorderBrush = BrushBorderNormal;
        editStaff.BorderBrush = BrushBorderNormal;
        editExecDate.BorderBrush = BrushBorderNormal;
        editResidence.BorderBrush = BrushBorderNormal;

        FadeIn(editOverlay);
        editName.Focus();
    }

    // 編集オーバーレイを閉じる（変更を破棄）
    private void CloseEditOverlay()
    {
        editingRecord = null;
        editCalendarPopup.IsOpen = false;
        FadeOut(editOverlay, delegate { dataTable.Focus(); });
    }

    // バリデーションを実行し、エラーメッセージを返す（正常時は null）
    private string ValidateEditFields()
    {
        if (string.IsNullOrWhiteSpace(editName.Text))
        {
            editName.BorderBrush = BrushError;
            return "氏名を入力してください";
        }
        if (string.IsNullOrWhiteSpace(editStaff.Text))
        {
            editStaff.BorderBrush = BrushError;
            return "処分担当を入力してください";
        }
        if (string.IsNullOrWhiteSpace(editResidence.Text))
        {
            editResidence.BorderBrush = BrushError;
            return "住民票住所を入力してください";
        }
        var execInput = editExecDate.Text.Trim();
        if (string.IsNullOrWhiteSpace(execInput) ||
            BusinessLogic.ParseFlexibleDate(execInput, eraMapping) == null)
        {
            editExecDate.BorderBrush = BrushError;
            return "有効な執行日を入力してください";
        }
        return null;
    }

    // 届出住所のリアルタイムバリデーション（チェックON時のみ50文字制限表示）
    private void ValidateEditDelivery()
    {
        if (editChkDelivery.IsChecked == true && !string.IsNullOrEmpty(editDelivery.Text))
        {
            int len = ("（届出：" + editDelivery.Text.Trim() + "）").Length;
            if (len > 50)
            {
                editDeliveryError.Text = "50文字を超えています（現在: " + len + "文字）";
                editDeliveryError.Visibility = Visibility.Visible;
                return;
            }
        }
        editDeliveryError.Visibility = Visibility.Collapsed;
    }

    // バリデーション → CSV 書き込み → 再読み込みを実行する
    private void SaveEdit()
    {
        // バリデーション状態のリセット
        editError.Text = "";
        editDeliveryError.Visibility = Visibility.Collapsed;
        editName.BorderBrush = BrushBorderNormal;
        editStaff.BorderBrush = BrushBorderNormal;
        editExecDate.BorderBrush = BrushBorderNormal;
        editResidence.BorderBrush = BrushBorderNormal;

        var errorMsg = ValidateEditFields();
        if (errorMsg != null) { editError.Text = errorMsg; return; }

        // 届出住所50文字チェック（チェックONの場合のみ）
        if (editChkDelivery.IsChecked == true && !string.IsNullOrEmpty(editDelivery.Text))
        {
            string deliveryFull = "（届出：" + editDelivery.Text.Trim() + "）";
            if (deliveryFull.Length > 50)
            {
                editDeliveryError.Text = "50文字を超えています（現在: " + deliveryFull.Length + "文字）";
                editDeliveryError.Visibility = Visibility.Visible;
                return;
            }
        }

        if (editingRecord == null) return;

        // 届出住所: チェックON →「（届出：...）」ラッパー付き / チェックOFF → 空文字
        string deliveryAddr = (editChkDelivery.IsChecked == true && !string.IsNullOrEmpty(editDelivery.Text.Trim()))
            ? "（届出：" + editDelivery.Text.Trim() + "）" : "";

        // 執行日: TextBox の yyyy/MM/dd → CSV 保存用の7桁和暦に変換
        var execDt = BusinessLogic.ParseFlexibleDate(editExecDate.Text.Trim(), eraMapping);
        string execWareki = execDt.HasValue
            ? BusinessLogic.DateToWareki(execDt.Value, eraMapping) : "";

        string csvPath = isDepositTab ? depositCsvPath : insuranceCsvPath;
        bool success = WriteEditedFields(csvPath, editingRecord.OriginalLineIndex,
            editName.Text.Trim(),
            editStaff.Text.Trim(),
            execWareki,
            editResidence.Text.Trim(),
            deliveryAddr);

        editingRecord = null;
        editCalendarPopup.IsOpen = false;

        FadeOut(editOverlay, delegate
        {
            if (success)
            {
                // CSV をディスクから再読み込み（他ユーザーの変更も反映）
                if (isDepositTab && depositCsvPath != null)
                    depositRecords = LoadCsvRecords(depositCsvPath, true);
                else if (!isDepositTab && insuranceCsvPath != null)
                    insuranceRecords = LoadCsvRecords(insuranceCsvPath, false);
                PopulateTable();
            }
            else
            {
                MessageBox.Show(
                    "CSV ファイルへの書き込みに失敗しました。\nファイルが他のプロセスで使用されている可能性があります。\n\n" +
                    "書き込みが途中で失敗した場合はバックアップ（.bak）が残っており、\n次回の読み込み時に復旧を確認します。",
                    "書き込みエラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            dataTable.Focus();
        });
    }

    // 指定行の編集対象フィールド（列[2]~[6]）を上書きする
    // WriteWithdrawalFlags と同一のバックアップ先行 + アトミック方式（WriteCsvWithBackup を共用）
    private bool WriteEditedFields(string csvPath, int targetLineIndex,
        string name, string staff, string execDate, string residence, string delivery)
    {
        return WriteCsvWithBackup(csvPath, delegate(string[] lines)
        {
            if (targetLineIndex >= lines.Length) return;

            var fields = BusinessLogic.ParseCsvLine(lines[targetLineIndex]);
            if (fields.Length <= EDIT_COL_DELIVERY) return;

            fields[EDIT_COL_NAME]      = name;
            fields[EDIT_COL_STAFF]     = staff;
            fields[EDIT_COL_EXEC_DATE] = execDate;
            fields[EDIT_COL_RESIDENCE]  = residence;
            fields[EDIT_COL_DELIVERY]   = delivery;

            lines[targetLineIndex] = string.Join(",",
                fields.Select(f => BusinessLogic.CsvEscape(f)));
        });
    }

    // ==============================================================
    // オーバーレイアニメーション
    // ==============================================================

    // オーバーレイのフェードイン（Opacity 0→1）
    private void FadeIn(Grid overlay)
    {
        overlay.BeginAnimation(UIElement.OpacityProperty, null);
        overlay.Opacity = 0;
        overlay.Visibility = Visibility.Visible;
        var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(150)));
        overlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // オーバーレイのフェードアウト（Opacity 1→0 → Collapsed）
    private void FadeOut(Grid overlay, Action onComplete = null)
    {
        var anim = new DoubleAnimation(1, 0, new Duration(TimeSpan.FromMilliseconds(150)));
        anim.Completed += delegate
        {
            overlay.BeginAnimation(UIElement.OpacityProperty, null);
            overlay.Opacity = 1;
            overlay.Visibility = Visibility.Collapsed;
            if (onComplete != null) onComplete();
        };
        overlay.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // ==============================================================
    // 再読み込み
    // ==============================================================

    // 現在のタブの CSV を再読み込みする
    private void ReloadCurrentTab()
    {
        if (isDepositTab && depositCsvPath != null)
            depositRecords = LoadCsvRecords(depositCsvPath, true);
        else if (!isDepositTab && insuranceCsvPath != null)
            insuranceRecords = LoadCsvRecords(insuranceCsvPath, false);

        searchBox.Text = "";
        columnCombo.SelectedIndex = 0;
        currentSortColumn = "文書番号";
        currentSortAsc = false;

        PopulateTable();
    }

    // ==============================================================
    // フッター更新
    // ==============================================================

    // フッターの件数情報と CSV パスを更新する
    private void UpdateFooter()
    {
        var allRecords = GetCurrentRecords();
        int total = allRecords.Count;
        int withdrawn = allRecords.Count(r => r.IsWithdrawn);
        int printed = allRecords.Count(r => r.IsPrinted);
        int displayed = dataTable.Items.Count;

        // 引抜済み（フラグ1=2）と印刷済み（フラグ1=1）は排他のため、内訳は重複しない
        statusLeft.Text = displayed + " 件 / " + total +
            " 件中（引抜済み: " + withdrawn + " 件・印刷済み: " + printed + " 件）";

        string path = isDepositTab ? depositCsvPath : insuranceCsvPath;
        statusRight.Text = path ?? "";
    }

    // ==============================================================
    // テーブルの空白エリアクリック処理
    // ==============================================================

    // VisualTree を辿って ListViewItem が見つからなければ選択を解除する
    private void OnTableEmptyAreaClick(object sender, MouseButtonEventArgs e)
    {
        var hitResult = VisualTreeHelper.HitTest(dataTable, e.GetPosition(dataTable));
        if (hitResult == null) { dataTable.SelectedItems.Clear(); return; }

        DependencyObject current = hitResult.VisualHit;
        while (current != null && !(current is ListViewItem))
            current = VisualTreeHelper.GetParent(current);

        if (current == null)
            dataTable.SelectedItems.Clear();
    }
}

// ==============================================================
// ICommand 実装（キーバインディング用）
// ==============================================================

public class RelayCommand : ICommand
{
    private readonly Action<object> _execute;
    public RelayCommand(Action<object> execute) { _execute = execute; }
    public event EventHandler CanExecuteChanged { add {} remove {} }
    public bool CanExecute(object parameter) { return true; }
    public void Execute(object parameter) { _execute(parameter); }
}
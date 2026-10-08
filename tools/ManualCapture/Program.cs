// 사용 설명서 화면 캡처 자동화
//
//   ManualCapture <AmrMapEditor.exe> <samples 폴더> <출력 폴더>
//
// 앱을 띄워 처음 쓰는 사람이 하는 순서대로 조작하면서
//   <출력>/shots/<이름>.png         스크린샷 (창 영역)
//   <출력>/frames/<이름>/0000.png   동영상 프레임 + cursor.txt (프레임 번호 · ms · 커서 x y · 버튼 눌림)
//   <출력>/shots.json               스크린샷별 창 안의 UI 요소 위치 (번호 표시용)
//   <출력>/log.txt
// 를 남긴다. 후처리(번호 표시 · GIF)는 make_assets.py.
// 단계 하나가 실패해도 나머지는 계속 진행하고 _fail_*.png를 남긴다.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using WRect = System.Windows.Rect;

namespace ManualCapture;

internal static class Program
{
    private static string _out = "";
    private static string _work = "";
    private static Process _app = null!;
    private static AutomationElement _win = null!;
    private static IntPtr _hwnd;
    private static readonly StringBuilder Log = new();
    private static readonly List<Dictionary<string, object>> Shots = new();
    private static Recorder? _rec;

    private static int Main(string[] args)
    {
        if (args.Length == 4 && args[0] == "--time") return TimeStartup(args[1], Path.GetFullPath(args[2]), int.Parse(args[3]));
        if (args.Length == 3 && args[0] == "--splash") return ShotSplash(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]));
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ManualCapture <AmrMapEditor.exe> <samples dir> <out dir>");
            return 2;
        }
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); // Per-Monitor V2: 좌표 = 실제 픽셀
        string exe = Path.GetFullPath(args[0]);
        _out = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.Combine(_out, "shots"));
        Directory.CreateDirectory(Path.Combine(_out, "frames"));

        // 샘플은 복사본으로 작업 (저장해도 저장소 파일은 그대로)
        _work = Path.Combine(Path.GetTempPath(), "amr-manual-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_work);
        foreach (string f in Directory.GetFiles(args[1])) File.Copy(f, Path.Combine(_work, Path.GetFileName(f)), true);

        Say($"screen {Native.GetSystemMetrics(0)} x {Native.GetSystemMetrics(1)}");
        if (Native.GetSystemMetrics(0) < 1600 || Native.GetSystemMetrics(1) < 1000)
        {
            // CI 러너 기본 해상도(1024 × 768)는 창이 다 들어가지 않는다
            var dm = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf<Native.DEVMODE>() };
            if (Native.EnumDisplaySettings(null, -1, ref dm))
            {
                dm.dmPelsWidth = 1920;
                dm.dmPelsHeight = 1080;
                dm.dmFields = 0x00080000 | 0x00100000;
                int r = Native.ChangeDisplaySettings(ref dm, 0);
                Thread.Sleep(2000);
                Say($"change display -> {r}, screen {Native.GetSystemMetrics(0)} x {Native.GetSystemMetrics(1)}");
            }
        }
        try
        {
            Launch(exe);
            Scenario();
        }
        catch (Exception ex)
        {
            Say("FATAL " + ex);
            TryShot("_fatal");
        }
        finally
        {
            try { _rec?.Stop(); } catch { }
            try { if (!_app.HasExited) _app.Kill(); } catch { }
            File.WriteAllText(Path.Combine(_out, "shots.json"),
                JsonSerializer.Serialize(Shots, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(_out, "log.txt"), Log.ToString());
        }
        return 0;
    }

    // ═════════════════════════ 시나리오 ═════════════════════════

    private static void Scenario()
    {
        Step("빈 화면", () =>
        {
            Park();
            Shot("01-empty");
        });

        // ── 맵 열기 ──
        Step("맵 열기", () =>
        {
            Key(VK.Control, (byte)'O');
            AutomationElement dlg = WaitDialog();
            Thread.Sleep(800);
            Shot("02-open-dialog");
            ChooseFile(dlg, Path.Combine(_work, "sample_new.pgm"));
            WaitIdle(1500);
            Park();
            Shot("03-main", "ToolbarRoot", "RailRoot", "MapViewer", "InspectorRoot", "ScopeBar", "SegEdit", "SegUpdate",
                "SegSecond", "SegClean", "SegDxf", "DisplayStandard", "StatusPos", "StatusZoom", "StatusMessage", "OpenButton",
                "SaveButton", "UndoButton", "ThemeToggle", "StatusBar", "RailSwatchButton", "ToolBrush", "ToolPillar", "FileNameText", "ValueFree");
        });

        Step("툴팁", () =>
        {
            WRect r = RectOf("ToolWall");
            MoveTo(r.Left + r.Width / 2, r.Top + r.Height / 2, 300);
            Thread.Sleep(1600);
            Shot("04-tooltip", "ToolWall");
            Park();
        });

        // ── 화면 이동 · 확대 ──
        Step("화면 조작", () =>
        {
            Calibrate();
            Record("navigate");
            (double x, double y) = M(300, 110);
            MoveTo(x, y, 500);
            Thread.Sleep(300);
            for (int i = 0; i < 4; i++) Wheel(120, 250);
            Thread.Sleep(600);
            MiddleDrag(x, y, x - 260, y + 140, 900);
            Thread.Sleep(600);
            for (int i = 0; i < 2; i++) Wheel(-120, 250);
            Thread.Sleep(600);
            Key((byte)'F');
            Thread.Sleep(900);
            StopRecord();
        });

        // ── 그리기 · 지우기 · 실행 취소 ──
        Step("그리기", () =>
        {
            Calibrate();
            Record("draw");
            Key((byte)'2'); // 그리기 값 = 장애물
            Thread.Sleep(300);
            Key((byte)'B');
            Thread.Sleep(400);
            (double ax, double ay) = M(150, 215);
            (double bx, double by) = M(250, 215);
            Drag(ax, ay, bx, by, 900);
            Thread.Sleep(400);
            (double cx, double cy) = M(250, 215);
            (double dx, double dy) = M(250, 280);
            Drag(cx, cy, dx, dy, 700);
            Thread.Sleep(500);
            Key((byte)'E');
            Thread.Sleep(400);
            (double ex, double ey) = M(185, 215);
            (double fx, double fy) = M(215, 215);
            Drag(ex, ey, fx, fy, 600);
            Thread.Sleep(700);
            Shot("05-draw");
            Key(VK.Control, (byte)'Z');
            Thread.Sleep(500);
            Key(VK.Control, (byte)'Z');
            Thread.Sleep(500);
            Key(VK.Control, (byte)'Z');
            Thread.Sleep(900);
            StopRecord();
            Key((byte)'B');
        });

        // ── 새 맵 정리 ──
        Step("정리 탭", () =>
        {
            Key(VK.Control, (byte)'4');
            Thread.Sleep(600);
            Park();
            Shot("10-clean-tab", "InspectorRoot", "SegClean");
        });

        // 작은 기둥(L자)은 노이즈 조건에도 걸리므로 노이즈 제거 전에 사각형으로 정리
        Step("기둥 정리", () =>
        {
            Calibrate();
            Key((byte)'C');
            Thread.Sleep(300);
            Record("pillar");
            Thread.Sleep(400);
            foreach ((double px, double py) in new[] { (117.0, 101.5), (217.0, 108.5), (317.0, 115.5), (106.0, 251.5), (306.0, 265.5) })
            {
                (double sx, double sy) = M(px, py);
                (int tx, int ty) = DarkNear((int)sx, (int)sy, 14);
                MoveTo(tx, ty, 450);
                Thread.Sleep(150);
                LeftClick();
                Thread.Sleep(500);
            }
            Thread.Sleep(900);
            StopRecord();
            Park();
            Shot("09-pillar");
        });

        Step("노이즈", () =>
        {
            Record("noise");
            ClickText("후보 찾기", 0);
            WaitIdle(1200);
            MoveTo(RectOf("MapViewer").Left + 40, RectOf("MapViewer").Bottom - 40, 300);
            Thread.Sleep(500);
            Shot("11-noise-found", "NoiseResults", "CandidateList", "InspectorRoot");
            ClickText("체크한 항목 삭제", 0);
            WaitIdle(1200);
            MoveTo(RectOf("MapViewer").Left + 40, RectOf("MapViewer").Bottom - 40, 300);
            Thread.Sleep(600);
            Shot("12-noise-deleted", "StatusMessage", "StatusUndoButton");
            StopRecord();
        });

        double angle = 0;
        int oldW = 460, oldH = 380, newW = 0, newH = 0;
        // 확인창 없이 실행 (Ctrl+Z로 되돌림) → 상태 표시줄에서 각도 · 크기를 읽음
        //   예: "기울기 보정 (+3.21°): 460 × 380 → 476 × 405 px · …"
        Step("기울기 보정", () =>
        {
            ClickText("주축에 맞춰 회전", 0);
            WaitIdle(2000);
            if (FindDialog() != null) throw new Exception("기울기 보정에서 대화상자가 뜸");
            string msg = StatusText();
            Match m = Regex.Match(msg, @"\(([+-]?\d+(?:\.\d+)?)°\)");
            if (m.Success) angle = double.Parse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            Match s = Regex.Match(msg, @"(\d+) × (\d+) → (\d+) × (\d+)");
            if (s.Success)
            {
                oldW = int.Parse(s.Groups[1].Value); oldH = int.Parse(s.Groups[2].Value);
                newW = int.Parse(s.Groups[3].Value); newH = int.Parse(s.Groups[4].Value);
            }
            Say($"deskew angle {angle} {oldW}x{oldH} -> {newW}x{newH}");
            Check(m.Success && s.Success, "기울기 보정", msg);
            Key((byte)'F');
            Thread.Sleep(800);
            Park();
            Shot("14-deskew-done", "StatusMessage", "StatusUndoButton");
        });

        // 기울기 보정 전 좌표 → 보정 후 좌표 (MainWindow.Cleanup.cs OnDeskew와 같은 식)
        (double, double) D(double px, double py)
        {
            if (newW == 0) return (px, py);
            double rad = angle * Math.PI / 180, cs = Math.Cos(rad), sn = Math.Sin(rad);
            double vx = px - oldW / 2.0, vy = py - oldH / 2.0;
            return (newW / 2.0 + vx * cs + vy * sn, newH / 2.0 - vx * sn + vy * cs);
        }

        Step("벽 직선화", () =>
        {
            Calibrate();
            // 가운데 칸막이 벽 쪽으로 확대
            (double wx, double wy) = D(165, 186);
            (double sx, double sy) = M(wx, wy);
            MoveTo(sx, sy, 300);
            Wheel(120, 200);
            Thread.Sleep(500);
            Calibrate();
            Key((byte)'W');
            Thread.Sleep(300);
            Record("straighten");
            Thread.Sleep(500);
            (double ax, double ay) = D(52, 170);
            (double bx, double by) = D(292, 202);
            (double s1x, double s1y) = M(Math.Min(ax, bx), Math.Min(ay, by) - 4);
            (double s2x, double s2y) = M(Math.Max(ax, bx), Math.Max(ay, by) + 4);
            Drag(s1x, s1y, s2x, s2y, 1300);
            WaitIdle(1500);
            MoveTo(s2x + 40, s2y + 80, 400);
            Thread.Sleep(1200);
            StopRecord();
            Shot("15-straighten", "StatusMessage");
        });

        // ── 회전 · 원점 (편집 탭) ──
        Step("원점 지정", () =>
        {
            Key(VK.Control, (byte)'1');
            Thread.Sleep(600);
            Key((byte)'F');
            Thread.Sleep(600);
            Calibrate();
            ClickText("맵에서 지정", 0);
            Thread.Sleep(400);
            (double wx, double wy) = D(60, 330);
            (double sx, double sy) = M(wx, wy);
            MoveTo(sx, sy, 400);
            Thread.Sleep(150);
            LeftClick();
            WaitIdle(800);
            string msg = StatusText();
            Check(msg.StartsWith("원점 지정", StringComparison.Ordinal), "원점 지정", msg);
            Park();
            Shot("17-origin", "OriginXBox", "OriginYBox", "StatusMessage", "MapViewer");
            Key(VK.Control, (byte)'Z');
            WaitIdle(800);
            Check(StatusText().StartsWith("실행 취소", StringComparison.Ordinal), "원점 실행 취소", StatusText());
        });

        Step("맵 회전", () =>
        {
            ClickText("180°", 0);
            WaitIdle(1500);
            string msg = StatusText();
            Check(msg.StartsWith("맵 회전", StringComparison.Ordinal), "맵 회전", msg);
            Key((byte)'F');
            Thread.Sleep(700);
            Park();
            Shot("18-rotate", "StatusMessage", "StatusUndoButton");
            Key(VK.Control, (byte)'Z');
            WaitIdle(1500);
            Check(StatusText().StartsWith("실행 취소", StringComparison.Ordinal), "회전 실행 취소", StatusText());
            Key(VK.Control, (byte)'Z');   // 기울기 보정까지 되돌림 (픽셀 기록 → 문서 상태 순서)
            WaitIdle(1500);
            Say("undo chain: " + StatusText());
            Key(VK.Control, (byte)'Y');
            WaitIdle(1500);
            Say("redo: " + StatusText());
        });

        Step("맵 크기", () =>
        {
            int w0 = int.Parse(BoxValue("ResizeWBox")), h0 = int.Parse(BoxValue("ResizeHBox"));
            int w1 = w0 + 100, h1 = h0 + 60;
            SetBoxValue("ResizeWBox", w1.ToString());
            SetBoxValue("ResizeHBox", h1.ToString());
            ClickText("크기 적용", 0);
            WaitIdle(1500);
            string msg = StatusText();
            Check(msg.StartsWith("맵 크기 변경", StringComparison.Ordinal) && msg.Contains($"→ {w1} × {h1} px"), "맵 크기", msg);
            Check(BoxValue("ResizeWBox") == w1.ToString() && BoxValue("ResizeHBox") == h1.ToString(), "맵 크기 입력칸",
                $"{BoxValue("ResizeWBox")} × {BoxValue("ResizeHBox")}");
            Key((byte)'F');
            Thread.Sleep(700);
            Park();
            Shot("19-resize", "ResizeWBox", "ResizeHBox", "StatusMessage", "MapViewer");
            Key(VK.Control, (byte)'Z');
            WaitIdle(1500);
            Check(StatusText().StartsWith("실행 취소", StringComparison.Ordinal) && BoxValue("ResizeWBox") == w0.ToString(),
                "크기 실행 취소", $"{StatusText()} / {BoxValue("ResizeWBox")} × {BoxValue("ResizeHBox")}");
        });

        Step("닫기", () =>
        {
            Key(VK.Control, (byte)'W');
            AutomationElement dlg = WaitDialog();
            Thread.Sleep(700);
            Shot("16-close-confirm", Dialog: dlg);
            ClickButtonIn(dlg, "저장 안 함");
            WaitIdle(800);
        });

        // ── 업데이트 보정 ──
        Step("업데이트 맵 열기", () =>
        {
            Key(VK.Control, (byte)'O');
            ChooseFile(WaitDialog(), Path.Combine(_work, "sample_updated.pgm"));
            WaitIdle(1200);
            Key(VK.Control, (byte)'2');
            Thread.Sleep(600);
            Park();
            Shot("20-update-empty", "InspectorRoot", "SegUpdate");
        });

        Step("기준 맵 열기", () =>
        {
            ClickText("기준 맵 열기", 0);
            ChooseFile(WaitDialog(), Path.Combine(_work, "sample_before.pgm"));
            WaitIdle(1500);
            Park();
            Shot("21-update-diff", "InspectorRoot", "RefChip", "DiffAddedText");
        });

        Step("업데이트 영역", () =>
        {
            Calibrate();
            Key((byte)'M');
            Thread.Sleep(300);
            Record("update-area");
            Thread.Sleep(400);
            (double ax, double ay) = M(30, 120);
            (double bx, double by) = M(370, 270);
            Drag(ax, ay, bx, by, 1300);
            Thread.Sleep(500);
            ClickEl(ById("AddAreaButton"));
            WaitIdle(900);
            StopRecord();
            Park();
            Shot("22-update-area", "AddAreaButton", "UpdateAreaText");
        });

        Step("영역 밖 복원", () =>
        {
            ClickEl(ById("RevertOutsideButton"));
            WaitIdle(900);
            Park();
            Shot("23-update-revert", "RevertOutsideButton", "StatusMessage");
        });

        Step("어긋남", () =>
        {
            ClickText("어긋남 추정", 0);
            WaitIdle(1500);
            Park();
            Shot("24-update-offset", "OffsetText", "RealignDxBox");
            ClickText("옮겨서 합치기", 0);
            WaitIdle(1200);
            Park();
            Shot("25-update-realign", "StatusMessage");
        });

        Step("이중 벽", () =>
        {
            ClickText("후보 찾기", 0);
            WaitIdle(1500);
            Park();
            Shot("26-update-dup", "DupResults", "DupList", "DupEmpty");
            // 옮겨서 합치기를 했으면 이중 벽이 이미 없어져 후보가 0개일 수 있다
            try
            {
                ClickText("체크한 항목 복원", 0);
                WaitIdle(1200);
                Park();
                Shot("27-update-dup-restored", "StatusMessage");
            }
            catch (Exception ex) { Say("dup restore skipped: " + ex.Message); }
        });

        Step("마무리 노이즈", () =>
        {
            Key(VK.Control, (byte)'4');
            Thread.Sleep(600);
            ClickText("후보 찾기", 0);
            WaitIdle(1200);
            ClickText("체크한 항목 삭제", 0);
            WaitIdle(1200);
            Key(VK.Control, (byte)'2');
            Thread.Sleep(600);
            ScrollInspectorTop();
            Park();
            Shot("28-update-done", "InspectorRoot");
        });

        Step("저장", () =>
        {
            Key(VK.Control, (byte)'S');
            Thread.Sleep(1200);
            AutomationElement? dlg = FindDialog();
            if (dlg != null)
            {
                Shot("29-save-confirm", Dialog: dlg);
                ClickButtonIn(dlg, "저장");
                WaitIdle(1200);
            }
            Park();
            Shot("30-saved", "StatusMessage", "DirtyChip");
        });

        Step("나머지 탭", () =>
        {
            Key(VK.Control, (byte)'3');
            Thread.Sleep(600);
            Park();
            Shot("40-tab-second", "InspectorRoot");
            Key(VK.Control, (byte)'5');
            Thread.Sleep(600);
            Park();
            Shot("41-tab-dxf", "InspectorRoot");
            Key(VK.Control, (byte)'1');
            Thread.Sleep(600);
        });

        Step("다크 모드", () =>
        {
            ClickEl(ById("ThemeToggle"));
            Thread.Sleep(1200);
            Park();
            Shot("42-dark");
            ClickEl(ById("ThemeToggle"));
            Thread.Sleep(800);
        });

        Step("정보 창", () =>
        {
            AutomationElement? about = _win.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                .Cast<AutomationElement>()
                .FirstOrDefault(e => (e.Current.HelpText ?? "").StartsWith("정보"));
            if (about == null) throw new Exception("정보 버튼 없음");
            ClickEl(about);
            AutomationElement dlg = WaitDialog();
            Thread.Sleep(800);
            Shot("43-about", Dialog: dlg);
            Park();   // 정보 버튼 툴팁이 남지 않게
            Key(VK.Escape);
            for (int i = 0; i < 10 && FindDialog() != null; i++) Thread.Sleep(200);
            AutomationElement? still = FindDialog();
            Check(still == null, "정보 창 Esc", still == null ? "Esc로 닫힘" : "Esc로 닫히지 않음 → 닫기 버튼으로 닫음");
            if (still != null)
            {
                ClickButtonIn(still, "닫기");
                Thread.Sleep(500);
                Check(FindDialog() == null, "정보 창 닫기 버튼", FindDialog() == null ? "닫힘" : "닫히지 않음");
            }
        });
    }

    // ═════════════════════════ 앱 · 창 ═════════════════════════

    private static void Launch(string exe)
    {
        (long splashAt, long mainAt) = StartAndWait(exe);
        // 빠른 PC에서는 첫 조회 전에 시작 화면이 이미 닫혔을 수 있어 못 봐도 실패는 아님
        Check(true, "시작 화면", splashAt >= 0 ? $"실행 후 {splashAt} ms에 시작 화면, {mainAt} ms에 메인 창" : $"시작 화면은 못 봄 · 메인 창 {mainAt} ms");
        Thread.Sleep(1500);

        int sw0 = Native.GetSystemMetrics(0), sh0 = Native.GetSystemMetrics(1);
        int w = Math.Min(1440, sw0 - 40), h = Math.Min(900, sh0 - 80);
        Native.ShowWindow(_hwnd, 1);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, 20, 20, w, h, 0x0040);
        Native.SetForegroundWindow(_hwnd);
        Thread.Sleep(1000);
        _win = AutomationElement.FromHandle(_hwnd);
        // 제목 표시줄을 한 번 눌러 키보드 포커스를 가져온다
        Rectangle wr = WinRect();
        MoveTo(wr.Left + wr.Width / 2.0, wr.Top + 14, 200);
        LeftClick();
        Thread.Sleep(500);
        Say($"window {wr}");
    }

    /// <summary>앱을 실행하고 시작 화면 · 메인 창(열기 버튼이 있는 창)이 뜰 때까지 기다림. 걸린 시간(ms, 못 보면 -1)</summary>
    private static (long SplashAt, long MainAt) StartAndWait(string exe)
    {
        _hwnd = IntPtr.Zero;
        _app = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _work, UseShellExecute = false })!;
        var sw = Stopwatch.StartNew();
        long splashAt = -1, mainAt = -1;
        var mine = new PropertyCondition(AutomationElement.ProcessIdProperty, _app.Id);
        var openBtn = new PropertyCondition(AutomationElement.AutomationIdProperty, "OpenButton");
        // 시작 화면(별도 창)이 먼저 뜨고, 메인 창이 뒤따름
        while (sw.ElapsedMilliseconds < 60000 && _hwnd == IntPtr.Zero)
        {
            foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, mine))
            {
                try
                {
                    if (top.FindFirst(TreeScope.Descendants, openBtn) != null)
                    {
                        _hwnd = new IntPtr(top.Current.NativeWindowHandle);
                        mainAt = sw.ElapsedMilliseconds;
                        break;
                    }
                    if (splashAt < 0 && top.Current.Name == "AMR Map Editor") splashAt = sw.ElapsedMilliseconds;
                }
                catch (ElementNotAvailableException) { }
            }
            if (_hwnd == IntPtr.Zero) Thread.Sleep(20);
        }
        if (_hwnd == IntPtr.Zero) throw new Exception("창이 뜨지 않음");
        return (splashAt, mainAt);
    }

    /// <summary>
    /// 시작 시간 측정: ManualCapture --time <이름> <exe> <횟수>
    /// 실행 → 시작 화면 · 메인 창까지 ms → 종료를 반복. 1회째는 첫 실행(압축 해제 · 검사), 그 뒤는 반복 실행
    /// </summary>
    private static int TimeStartup(string name, string exe, int runs)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        _work = Path.GetTempPath();
        long size = new FileInfo(exe).Length;
        var rows = new List<string>();
        for (int i = 1; i <= runs; i++)
        {
            (long splashAt, long mainAt) = StartAndWait(exe);
            Thread.Sleep(500);
            try { _app.Kill(); _app.WaitForExit(5000); } catch (Exception) { }
            string row = $"{name}\t{size}\t{i}\t{splashAt}\t{mainAt}";
            rows.Add(row);
            Console.WriteLine(row);
            Thread.Sleep(1000);
        }
        Console.WriteLine($"::notice title=시작 시간 {name} ({size / 1048576.0:0.0} MB)::" +
            string.Join(" · ", rows.ConvertAll(r => { string[] c = r.Split('\t'); return $"{c[2]}회 시작 화면 {c[3]} ms / 메인 창 {c[4]} ms"; })));
        return 0;
    }

    /// <summary>
    /// 시작 화면 캡처: ManualCapture --splash <exe> <out dir>
    /// 설정 파일로 테마 · 지난 시작 시간을 꾸며 두 가지(라이트 · 남은 시간 있음 / 다크 · 첫 실행)를 찍는다
    /// </summary>
    private static int ShotSplash(string exe, string outDir)
    {
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        _work = Path.GetTempPath();
        Directory.CreateDirectory(Path.Combine(outDir, "shots"));
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AmrMapEditor", "settings.json");
        string? backup = File.Exists(settings) ? File.ReadAllText(settings) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        Environment.SetEnvironmentVariable("AMRMAPEDITOR_SPLASH_HOLD", "6000");
        try
        {
            foreach ((string name, string json, int waitMs) in new[]
            {
                ("splash-light", "{\"Values\":{\"Theme\":\"Light\",\"StartupMs\":\"7000\"}}", 2500),
                ("splash-dark-first", "{\"Values\":{\"Theme\":\"Dark\"}}", 1500),
            })
            {
                File.WriteAllText(settings, json);
                _hwnd = IntPtr.Zero;
                _app = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = _work, UseShellExecute = false })!;
                var mine = new PropertyCondition(AutomationElement.ProcessIdProperty, _app.Id);
                var openBtn = new PropertyCondition(AutomationElement.AutomationIdProperty, "OpenButton");
                AutomationElement? splash = null;
                var sw = Stopwatch.StartNew();
                while (splash == null && sw.ElapsedMilliseconds < 60000)
                {
                    foreach (AutomationElement top in AutomationElement.RootElement.FindAll(TreeScope.Children, mine))
                    {
                        try
                        {
                            if (top.Current.Name == "AMR Map Editor" && top.FindFirst(TreeScope.Descendants, openBtn) == null) { splash = top; break; }
                        }
                        catch (ElementNotAvailableException) { }
                    }
                    if (splash == null) Thread.Sleep(50);
                }
                if (splash == null) throw new Exception("시작 화면이 뜨지 않음");
                Thread.Sleep(waitMs);
                WRect r = splash.Current.BoundingRectangle;
                var area = new Rectangle((int)r.Left - 12, (int)r.Top - 12, (int)r.Width + 24, (int)r.Height + 24);
                Save(Grab(area), Path.Combine(outDir, "shots", name + ".png"));
                Say($"shot {name} {area}");
                try { _app.Kill(); _app.WaitForExit(5000); } catch (Exception) { }
                Thread.Sleep(500);
            }
        }
        finally
        {
            if (backup != null) File.WriteAllText(settings, backup); else File.Delete(settings);
        }
        return 0;
    }

    private static Rectangle WinRect()
    {
        if (Native.DwmGetWindowAttribute(_hwnd, 9, out Native.RECT r, Marshal.SizeOf<Native.RECT>()) == 0)
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        WRect b = _win.Current.BoundingRectangle;
        return new Rectangle((int)b.Left, (int)b.Top, (int)b.Width, (int)b.Height);
    }

    /// <summary>앱의 모달 창(파일 대화상자 · 확인창 · 정보 창)</summary>
    private static AutomationElement? FindDialog()
    {
        var cond = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
            new PropertyCondition(AutomationElement.ProcessIdProperty, _app.Id));
        foreach (AutomationElement w in _win.FindAll(TreeScope.Children, cond))
            if (!IsPopup(w)) return w;
        foreach (AutomationElement w in AutomationElement.RootElement.FindAll(TreeScope.Children, cond))
            if (w.Current.NativeWindowHandle != _hwnd.ToInt32() && !IsPopup(w)) return w;
        return null;
    }

    /// <summary>툴팁 · 드롭다운 같은 WPF 팝업 창은 대화상자가 아님</summary>
    private static bool IsPopup(AutomationElement w)
    {
        try
        {
            string cls = w.Current.ClassName ?? "";
            return cls.Contains("Popup", StringComparison.OrdinalIgnoreCase) || cls.Contains("ToolTip", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static AutomationElement WaitDialog(int timeoutMs = 10000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            AutomationElement? d = FindDialog();
            if (d != null) return d;
            Thread.Sleep(200);
        }
        throw new Exception("대화상자가 뜨지 않음");
    }

    private static void ChooseFile(AutomationElement dlg, string path)
    {
        Thread.Sleep(600);
        var edit = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1148"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
        if (edit == null) throw new Exception("파일 이름 칸 없음");
        ((ValuePattern)edit.GetCurrentPattern(ValuePattern.Pattern)).SetValue(path);
        Thread.Sleep(300);
        var ok = dlg.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.AutomationIdProperty, "1"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
        if (ok != null) ((InvokePattern)ok.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        else Key(VK.Return);
        var sw = Stopwatch.StartNew();
        while (FindDialog() != null && sw.ElapsedMilliseconds < 8000) Thread.Sleep(200);
    }

    private static void ClickButtonIn(AutomationElement dlg, string name)
    {
        var b = dlg.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            .Cast<AutomationElement>()
            .FirstOrDefault(e => !e.Current.BoundingRectangle.IsEmpty)
            ?? throw new Exception($"대화상자 버튼 '{name}' 없음");
        ClickEl(b, scroll: false);
    }

    private static AutomationElement ById(string id) =>
        _win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id))
        ?? throw new Exception($"요소 '{id}' 없음");

    /// <summary>
    /// 요소 위치. Border · 맵 컨트롤처럼 UI Automation에 나오지 않는 영역은 주변 요소 위치로 계산한다
    /// (도구 막대 높이 48 · 상태 표시줄 28 · 도구 열은 도구 버튼 + 8 · 오른쪽 패널은 세그먼트 탭 기준)
    /// </summary>
    private static WRect RectOf(string id)
    {
        switch (id)
        {
            case "ToolbarRoot" or "RailRoot" or "MapViewer" or "InspectorRoot" or "InspectorBody" or "StatusBar":
                Rectangle w = WinRect();
                WRect open = ById("OpenButton").Current.BoundingRectangle;
                WRect brush = ById("ToolBrush").Current.BoundingRectangle;
                WRect seg = ById("SegEdit").Current.BoundingRectangle;
                double tbTop = open.Top - 8, tbBottom = tbTop + 48, stTop = w.Bottom - 29;
                double railRight = brush.Right + 8, inspLeft = seg.Left - 15;
                return id switch
                {
                    "ToolbarRoot" => new WRect(w.Left, tbTop, w.Width, 48),
                    "RailRoot" => new WRect(w.Left, tbBottom, railRight - w.Left, stTop - tbBottom),
                    "MapViewer" => new WRect(railRight, tbBottom, inspLeft - railRight, stTop - tbBottom),
                    "InspectorRoot" => new WRect(inspLeft, tbBottom, w.Right - inspLeft, stTop - tbBottom),
                    "InspectorBody" => new WRect(inspLeft, seg.Bottom + 52, w.Right - inspLeft, stTop - seg.Bottom - 52),
                    _ => new WRect(w.Left, stTop, w.Width, w.Bottom - stTop),
                };
            default:
                return ById(id).Current.BoundingRectangle;
        }
    }

    /// <summary>보이는 탭에서 글자가 name인 n번째 요소(버튼 또는 버튼 안 글자)를 누른다</summary>
    private static void ClickText(string name, int n)
    {
        WRect insp = RectOf("InspectorRoot");
        var list = _win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name))
            .Cast<AutomationElement>()
            .Where(e =>
            {
                WRect r = e.Current.BoundingRectangle;
                return !r.IsEmpty && r.Left >= insp.Left - 1 && r.Right <= insp.Right + 1;
            })
            .ToList();
        // 버튼과 그 안의 글자가 둘 다 잡히면 같은 위치이므로 위치로 중복 제거
        var uniq = new List<AutomationElement>();
        foreach (AutomationElement e in list)
        {
            WRect r = e.Current.BoundingRectangle;
            if (uniq.All(u => Math.Abs(u.Current.BoundingRectangle.Top - r.Top) > 6)) uniq.Add(e);
        }
        if (uniq.Count <= n) throw new Exception($"'{name}' #{n} 없음 (찾은 개수 {uniq.Count})");
        ClickEl(uniq[n]);
    }

    private static void ClickEl(AutomationElement el, bool scroll = true)
    {
        if (scroll) EnsureVisible(el);
        WRect r = el.Current.BoundingRectangle;
        if (r.IsEmpty) throw new Exception("보이지 않는 요소");
        MoveTo(r.Left + r.Width / 2, r.Top + r.Height / 2, 350);
        Thread.Sleep(120);
        LeftClick();
        Thread.Sleep(250);
    }

    /// <summary>오른쪽 패널 스크롤 안에서 요소가 보이도록 휠로 굴린다</summary>
    private static void EnsureVisible(AutomationElement el)
    {
        WRect body = RectOf("InspectorBody");
        WRect r0 = el.Current.BoundingRectangle;
        if (r0.Left < body.Left - 1 || r0.Right > body.Right + 1) return; // 패널 밖 요소
        for (int i = 0; i < 40; i++)
        {
            WRect r = el.Current.BoundingRectangle;
            int dir = r.Bottom > body.Bottom - 8 ? -1 : r.Top < body.Top + 8 ? 1 : 0;
            if (dir == 0) return;
            MoveTo(body.Left + body.Width / 2, body.Top + body.Height / 2, 120);
            Wheel(120 * dir, 90);
        }
    }

    private static void ScrollInspectorTop()
    {
        WRect body = RectOf("InspectorBody");
        MoveTo(body.Left + body.Width / 2, body.Top + body.Height / 2, 150);
        for (int i = 0; i < 25; i++) Wheel(120, 40);
    }

    /// <summary>맵 위가 아닌 제목 표시줄로 커서를 치워 툴팁 · 미리보기가 안 찍히게</summary>
    private static void Park()
    {
        Rectangle wr = WinRect();
        MoveTo(wr.Left + wr.Width * 0.62, wr.Top + 14, 200);
        Thread.Sleep(500);
    }

    /// <summary>진행 표시가 사라질 때까지 (최소 ms)</summary>
    private static void WaitIdle(int ms)
    {
        Thread.Sleep(ms);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 20000)
        {
            try
            {
                if (ById("StatusProgress").Current.IsOffscreen || ById("StatusProgress").Current.BoundingRectangle.IsEmpty) return;
            }
            catch { return; }
            Thread.Sleep(200);
        }
    }

    // ═════════════════════════ 맵 좌표 ═════════════════════════

    private static double _kx = 1, _ky = 1, _ox, _oy;

    /// <summary>커서를 두 곳에 올려 상태 표시줄의 픽셀 좌표를 읽고 맵 픽셀 → 화면 변환을 구한다</summary>
    private static void Calibrate()
    {
        WRect r = RectOf("MapViewer");
        double cx = r.Left + r.Width / 2, cy = r.Top + r.Height / 2, d = Math.Min(r.Width, r.Height) * 0.18;
        (int x1, int y1) = ReadPos(cx - d, cy - d);
        (int x2, int y2) = ReadPos(cx + d, cy + d);
        if (x1 == x2 || y1 == y2) throw new Exception("보정 실패");
        _kx = 2 * d / (x2 - x1);
        _ky = 2 * d / (y2 - y1);
        _ox = cx - d - _kx * (x1 + 0.5);
        _oy = cy - d - _ky * (y1 + 0.5);
        Say($"calibrate k=({_kx:0.000},{_ky:0.000}) o=({_ox:0.0},{_oy:0.0})");
    }

    private static (int, int) ReadPos(double sx, double sy)
    {
        MoveTo(sx, sy, 150);
        Thread.Sleep(250);
        string t = ById("StatusPos").Current.Name;
        Match m = Regex.Match(t, @"(-?\d+),\s*(-?\d+)");
        if (!m.Success) throw new Exception($"커서 좌표 읽기 실패: '{t}'");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    private static (double, double) M(double px, double py) => (_ox + _kx * (px + 0.5), _oy + _ky * (py + 0.5));

    /// <summary>화면에서 (x, y) 근처의 가장 가까운 검정(장애물) 픽셀</summary>
    private static (int, int) DarkNear(int x, int y, int radius)
    {
        using var bmp = new Bitmap(radius * 2 + 1, radius * 2 + 1);
        using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(x - radius, y - radius, 0, 0, bmp.Size);
        int best = int.MaxValue, bx = x, by = y;
        for (int j = 0; j < bmp.Height; j++)
        for (int i = 0; i < bmp.Width; i++)
        {
            Color c = bmp.GetPixel(i, j);
            if (c.R + c.G + c.B > 120) continue;
            int dx = i - radius, dy = j - radius, d2 = dx * dx + dy * dy;
            if (d2 < best) { best = d2; bx = x + dx; by = y + dy; }
        }
        return (bx, by);
    }

    // ═════════════════════════ 캡처 ═════════════════════════

    private static void Step(string name, Action a)
    {
        Say($"── {name}");
        try
        {
            a();
            // 단계가 끝났는데 대화상자가 남아 있으면 (예상하지 못한 오류 창 등) 경고로 남김
            AutomationElement? left = FindDialog();
            if (left != null)
            {
                string text = string.Join(" / ", left.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                    .Cast<AutomationElement>().Select(e => e.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n)).Take(8));
                Console.WriteLine($"::warning title=대화상자 남음 ({name})::{text}");
                Say($"DIALOG LEFT {name}: {text}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"::warning title=단계 실패 ({name})::{ex.Message}");
            Say($"FAIL {name}: {ex.Message}\n{ex.StackTrace}");
            try { _rec?.Stop(); } catch { }
            _rec = null;
            TryShot("_fail_" + name);
            // 남은 대화상자를 닫고 다음 단계로
            for (int i = 0; i < 3 && FindDialog() != null; i++) { Key(VK.Escape); Thread.Sleep(500); }
        }
    }

    private static void TryShot(string name)
    {
        try
        {
            Rectangle r = _hwnd != IntPtr.Zero ? WinRect() : new Rectangle(0, 0, Native.GetSystemMetrics(0), Native.GetSystemMetrics(1));
            Save(Grab(r), Path.Combine(_out, "shots", name + ".png"));
        }
        catch { }
    }

    private static void Shot(string name, params string[] ids) => Shot(name, null, ids);

    private static void Shot(string name, AutomationElement? Dialog, params string[] ids)
    {
        Rectangle wr = WinRect();
        Rectangle area = wr;
        if (Dialog != null)
        {
            WRect d = Dialog.Current.BoundingRectangle;
            area = Rectangle.Union(wr, new Rectangle((int)d.Left, (int)d.Top, (int)d.Width, (int)d.Height));
        }
        Save(Grab(area), Path.Combine(_out, "shots", name + ".png"));

        var rects = new Dictionary<string, int[]>();
        foreach (string id in ids)
        {
            try
            {
                WRect r = RectOf(id);
                if (!r.IsEmpty) rects[id] = new[] { (int)r.Left - area.Left, (int)r.Top - area.Top, (int)r.Width, (int)r.Height };
            }
            catch { }
        }
        if (Dialog != null)
        {
            WRect d = Dialog.Current.BoundingRectangle;
            rects["dialog"] = new[] { (int)d.Left - area.Left, (int)d.Top - area.Top, (int)d.Width, (int)d.Height };
        }
        rects["window"] = new[] { wr.Left - area.Left, wr.Top - area.Top, wr.Width, wr.Height };
        Shots.Add(new Dictionary<string, object> { ["name"] = name, ["rects"] = rects });
        Say($"shot {name}");
    }

    private static Bitmap Grab(Rectangle r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format24bppRgb);
        using Graphics g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size);
        return bmp;
    }

    private static void Save(Bitmap b, string path)
    {
        b.Save(path, ImageFormat.Png);
        b.Dispose();
    }

    private static void Record(string name)
    {
        _rec?.Stop();
        _rec = new Recorder(Path.Combine(_out, "frames", name), WinRect());
        _rec.Start();
        Thread.Sleep(400);
    }

    private static void StopRecord()
    {
        _rec?.Stop();
        _rec = null;
    }

    /// <summary>상태 표시줄 문구 (TextBlock 자동화 이름 = 글자)</summary>
    private static string StatusText()
    {
        try { return ById("StatusMessage").Current.Name ?? ""; }
        catch { return ""; }
    }

    /// <summary>입력칸 값 읽기 · 쓰기 (ValuePattern)</summary>
    private static string BoxValue(string id) =>
        ((ValuePattern)ById(id).GetCurrentPattern(ValuePattern.Pattern)).Current.Value ?? "";

    private static void SetBoxValue(string id, string value)
    {
        AutomationElement el = ById(id);
        EnsureVisible(el);
        ((ValuePattern)el.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
        Thread.Sleep(150);
    }

    /// <summary>동작 확인: 결과를 Actions 주석(annotation)으로 남김</summary>
    private static void Check(bool ok, string what, string detail)
    {
        Console.WriteLine(ok ? $"::notice title=확인 {what}::{detail}" : $"::warning title=확인 실패 {what}::{detail}");
        Say($"{(ok ? "OK" : "NG")} {what}: {detail}");
    }

    private static void Say(string s)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {s}";
        Console.WriteLine(line);
        Log.AppendLine(line);
    }

    // ═════════════════════════ 입력 ═════════════════════════

    internal static volatile bool MouseDown;

    private static void MoveTo(double x, double y, int ms)
    {
        Native.GetCursorPos(out Native.POINT p);
        int steps = Math.Max(1, ms / 15);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double e = t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            Native.SetCursorPos((int)Math.Round(p.X + (x - p.X) * e), (int)Math.Round(p.Y + (y - p.Y) * e));
            Thread.Sleep(15);
        }
    }

    private static void LeftClick()
    {
        MouseDown = true;
        Native.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(90);
        Native.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        MouseDown = false;
    }

    private static void Drag(double x1, double y1, double x2, double y2, int ms)
    {
        MoveTo(x1, y1, 400);
        Thread.Sleep(150);
        MouseDown = true;
        Native.mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
        MoveTo(x2, y2, ms);
        Thread.Sleep(150);
        Native.mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
        MouseDown = false;
    }

    private static void MiddleDrag(double x1, double y1, double x2, double y2, int ms)
    {
        MoveTo(x1, y1, 200);
        Native.mouse_event(0x0020, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(120);
        MoveTo(x2, y2, ms);
        Thread.Sleep(120);
        Native.mouse_event(0x0040, 0, 0, 0, UIntPtr.Zero);
    }

    private static void Wheel(int delta, int pauseMs)
    {
        Native.mouse_event(0x0800, 0, 0, delta, UIntPtr.Zero);
        Thread.Sleep(pauseMs);
    }

    private static class VK
    {
        public const byte Control = 0x11, Shift = 0x10, Return = 0x0D, Escape = 0x1B;
    }

    private static void Key(params byte[] keys)
    {
        foreach (byte k in keys) { Native.keybd_event(k, 0, 0, UIntPtr.Zero); Thread.Sleep(30); }
        Thread.Sleep(50);
        foreach (byte k in keys.Reverse()) { Native.keybd_event(k, 0, 2, UIntPtr.Zero); Thread.Sleep(30); }
        Thread.Sleep(150);
    }
}

/// <summary>창 영역을 일정 간격으로 PNG 프레임으로 저장 (커서는 cursor.txt에 따로 기록, 후처리에서 그림)</summary>
internal sealed class Recorder
{
    private readonly string _dir;
    private readonly Rectangle _area;
    private readonly List<string> _log = new();
    private Thread? _thread;
    private volatile bool _run;

    public Recorder(string dir, Rectangle area)
    {
        _dir = dir;
        _area = area;
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
    }

    public void Start()
    {
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_run) return;
        _run = false;
        _thread?.Join();
        File.WriteAllLines(Path.Combine(_dir, "cursor.txt"), _log);
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        int i = 0;
        while (_run)
        {
            long t0 = sw.ElapsedMilliseconds;
            Native.GetCursorPos(out Native.POINT p);
            using (var bmp = new Bitmap(_area.Width, _area.Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(_area.Left, _area.Top, 0, 0, _area.Size);
                bmp.Save(Path.Combine(_dir, $"{i:0000}.png"), ImageFormat.Png);
            }
            _log.Add($"{i} {t0} {p.X - _area.Left} {p.Y - _area.Top} {(Program.MouseDown ? 1 : 0)}");
            i++;
            long wait = 80 - (sw.ElapsedMilliseconds - t0);
            if (wait > 0) Thread.Sleep((int)wait);
        }
    }
}

internal static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string? device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ChangeDisplaySettings(ref DEVMODE dm, int flags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
}

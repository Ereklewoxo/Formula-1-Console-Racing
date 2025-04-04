using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.Wave;
using static Racing;
public static partial class Keyboard
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
    public static bool IsKeyPressed(ConsoleKey key)
    {
        return (GetAsyncKeyState((int)key) & 0x8000) != 0;
    }
}
public class CircuitSegment(float curvature, float distance, int cornerNumber, CornerType cornerType, bool isReal)
{
    public float Curvature { get; set; } = curvature;
    public float Distance { get; set; } = distance;
    public int CornerNumber { get; set; } = cornerNumber;
    public CornerType CornerType { get; set; } = cornerType;
    public bool IsReal { get; set; } = isReal;
}
public partial class Racing
{
    #region Pre
    public static partial class CustomColor
    {
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetConsoleMode(IntPtr hConsoleHandle, int mode);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetConsoleMode(IntPtr handle, out int mode);
        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr GetStdHandle(int handle);
        public static void Color()
        {
            var handle = GetStdHandle(-11);
            GetConsoleMode(handle, out int mode);
            SetConsoleMode(handle, mode | 0x4);
        }
    }

    public const int VK_F11 = 0x7A;

    public const uint WM_KEYDOWN = 0x100;

    public const int SW_MAXIMIZE = 3;
    [DllImport("kernel32.dll")]
    public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    public static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    public static void SetConsoleFullscreen()
    {
        var hwnd = GetConsoleWindow();
        PostMessage(hwnd, WM_KEYDOWN, VK_F11, IntPtr.Zero);
    }
    public static void SetConsoleBufferSizeToWindowSize()
    {
        Console.SetWindowSize(Console.LargestWindowWidth, Console.LargestWindowHeight);
        Console.SetBufferSize(Console.LargestWindowWidth, Console.LargestWindowHeight);
        Console.SetWindowSize(Console.LargestWindowWidth, Console.LargestWindowHeight);
    }
    #endregion
    static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        CustomColor.Color();
        SetConsoleFullscreen();
        SetConsoleBufferSizeToWindowSize();
        Console.CursorVisible = false;
        Console.Write("\x1b[48;2;65;120;200m");

        Console.ReadKey(false);

        Renderer renderer = new(Console.LargestWindowWidth, Console.LargestWindowHeight);
        TrackRenderer trackRenderer = new(renderer);
        SceneryRenderer sceneryRenderer = new(renderer);
        HudRenderer hudRenderer = new(renderer);
        SectorManager sectorManager = new();

        Car playerCar = new();

        var vecTrack = CircuitVecs;

        int direction, nTrackSection, nCarX, nCarY = Console.LargestWindowHeight - 15;

        long elapsedTime;

        float fCurvature = 0.0f, fTrackCurvature = 0.0f, fPlayerCurvature = 0.0f, fTrackDistance = 0.0f,
              fDistance = 0.0f, fCarPos = 0.0f, turnRate, fOffset, fTargetCurvature, fTrackCurveDiff,
              lowSpeedThreshold = 0.15f, highSpeedThreshold = 0.3f;

        const int targetFps = 39,
                  frameTime = 1000 / targetFps;

        const float fElapsedTime = (float)frameTime / 1000;

        foreach (var t in vecTrack)
            fTrackDistance += t.Distance;

        Stopwatch stopwatch = new();
        stopwatch.Start();

        Stopwatch keyDelay = new();
        keyDelay.Start();

        Stopwatch time = new();
        time.Start();

        EngineSound engineSound = new();
        engineSound.StartSound(); // Start engine sound
        EngineSound engineSound2 = new();
        engineSound2.StartSound(); // Start engine sound
        EngineSound engineSound3 = new();
        engineSound3.StartSound(); // Start engine sound
        EngineSound engineSound4 = new();
        engineSound4.StartSound(); // Start engine sound

        while (true)
        {
            stopwatch.Restart();

            direction = 0;
            turnRate = CalculateTrunRate(lowSpeedThreshold, highSpeedThreshold, fElapsedTime, playerCar);

            #region controls
            if (Keyboard.IsKeyPressed(ConsoleKey.W))
                playerCar.Accelerate(1.0f, fElapsedTime);
            else
                playerCar.Decelerate(0.4f, fElapsedTime);
            if (Keyboard.IsKeyPressed(ConsoleKey.A))
            {
                direction = 2;
                fPlayerCurvature -= playerCar.Reverse ? -turnRate : turnRate;
                if (playerCar.Gear != 1)
                    playerCar.Decelerate(0.01f, fElapsedTime);
            }
            if (Keyboard.IsKeyPressed(ConsoleKey.D))
            {
                direction = 1;
                fPlayerCurvature += playerCar.Reverse ? -turnRate : turnRate;
                if (playerCar.Gear != 1)
                    playerCar.Decelerate(0.01f, fElapsedTime);
            }
            if (Keyboard.IsKeyPressed(ConsoleKey.S))
                playerCar.Decelerate(3.0f, fElapsedTime);
            if (Keyboard.IsKeyPressed(ConsoleKey.Spacebar))
                playerCar.ClutchEngaged = true;
            if (Keyboard.IsKeyPressed(ConsoleKey.R) && keyDelay.ElapsedMilliseconds > 350)
            {
                playerCar.ReverseGear();
                keyDelay.Restart();
            }
            // Automatic Gearbox
            if (playerCar.Speed >= Car.GearMaxSpeed[playerCar.Gear] / 375.0f)
                playerCar.ShiftUp();
            else if (playerCar.Gear > 0 && playerCar.Speed <= Car.GearMaxSpeed[playerCar.Gear - 1] / 375.0f)
                playerCar.ShiftDown();
            #endregion

            if ((fCarPos * fCurvature > 0 && Math.Abs(fCarPos) > 0.6f) ||
                (Math.Abs(fCarPos - fCurvature) > 0.6f && !playerCar.Reverse))
            {
                playerCar.Speed += (playerCar.Reverse ? 1 : -1) *
                             Math.Abs(fCurvature * 5 * playerCar.Speed * (playerCar.Gear / 5) + 1.0f) *
                             fElapsedTime;
            }

            playerCar.Speed = Math.Clamp(playerCar.Speed,
                                   playerCar.Reverse ? Car.GearMaxSpeed[0] / 375.0f : 0.0f,
                                   playerCar.Reverse ? 0.0f : Car.GearMaxSpeed[playerCar.Gear] / 375.0f);

            playerCar.UpdateRPM();

            engineSound.UpdateEngineState(playerCar.RPM, playerCar.NormalizeGear(playerCar.Gear), playerCar.Acceleration);
            engineSound2.UpdateEngineState(playerCar.RPM, playerCar.NormalizeGear(playerCar.Gear), playerCar.Acceleration);
            engineSound3.UpdateEngineState(playerCar.RPM, playerCar.NormalizeGear(playerCar.Gear), playerCar.Acceleration);
            engineSound4.UpdateEngineState(playerCar.RPM, playerCar.NormalizeGear(playerCar.Gear), playerCar.Acceleration);

            fOffset = 0;
            nTrackSection = 0;

            while (nTrackSection < vecTrack.Count - 1 && fOffset <= fDistance)
            {
                fOffset += vecTrack[nTrackSection].Distance;
                nTrackSection++;
            }

            fDistance += playerCar.Speed * 130 * fElapsedTime;

            sectorManager.UpdateSector(vecTrack[nTrackSection].CornerNumber, time.Elapsed, fDistance, fTrackDistance);

            if (fDistance >= fTrackDistance)
            {
                sectorManager.ResetLap(ref fDistance, fTrackDistance, time);
            }

            if (fDistance < 0)
            {
                fDistance = 0;
                playerCar.Speed = 0;
            }

            sectorManager.UpdateSectorHUD();

            var (metersLeft, nextCornertype) = CheckForUpcomingCorner(fDistance);

            if (nTrackSection > 0)
            {
                fTargetCurvature = vecTrack[nTrackSection - 1].Curvature;
                fTrackCurveDiff = (fTargetCurvature - fCurvature) * (fElapsedTime * playerCar.Speed);
                fCurvature += fTrackCurveDiff;
                fTrackCurvature += fCurvature * playerCar.Speed * fElapsedTime;

                trackRenderer.DrawTrack(fCurvature, fDistance, fTrackDistance);
                sceneryRenderer.DrawScenery(fTrackCurvature);
            }

            fCarPos = fPlayerCurvature - fTrackCurvature * 9.0f;
            nCarX = Console.LargestWindowWidth / 2 + ((int)(Console.LargestWindowWidth * fCarPos) / 2) - 20;
            renderer.DrawCar(nCarX, nCarY, "#E8002D", direction, fDistance);

            hudRenderer.TimesHud(sectorManager.GetCurrentHudTime(time), sectorManager.BestTimes[0]);
            hudRenderer.DrawCircuitMap(Console.LargestWindowWidth / 2 - HudRenderer.CircuitSprite[0].Length / 2, 1, "#FFFFFF", "#E8002D",
                (int)fDistance, nTrackSection, (int)vecTrack[nTrackSection - 1].Distance, (int)fOffset);
            hudRenderer.DrawNextCorner(Console.LargestWindowWidth / 2 + HudRenderer.CornerTypeGraphic[0].Length + 24, 3, "#FFFFFF", metersLeft, nextCornertype);
            hudRenderer.HudBuilder(playerCar);
            hudRenderer.SectorsHud(sectorManager.SectorColors);

            renderer.DisplayFrame();
            elapsedTime = stopwatch.ElapsedMilliseconds;
            if (elapsedTime < frameTime)
                Thread.Sleep((int)(frameTime - elapsedTime));
        }
    }
    public static (float metersBeforeCorner, CornerType) CheckForUpcomingCorner(float currentDistance)
    {
        float distanceCovered = 0.0f;

        foreach (var segment in CircuitVecs)
        {
            distanceCovered += segment.Distance;

            if (segment.IsReal && distanceCovered > currentDistance && segment.CornerType != 0) 
            {
                float metersBeforeCorner = distanceCovered - currentDistance;
                return (metersBeforeCorner, segment.CornerType);
            }
        }
        return (0.0f, 0);
    }
    public enum CornerType
    {
        None = 0,
        ChicaneR = 1,
        ChicaneL = 2,
        HairpinR = 3,
        HairpinL = 4,
        NormalR = 5, 
        NormalL = 6
    }
    public static readonly List<CircuitSegment> CircuitVecs =
    [
        new CircuitSegment(0.0f, 550.0f, 0, 0, false),
        new CircuitSegment(0.9f, 70.0f, 1, CornerType.ChicaneR, true),
        new CircuitSegment(-0.9f, 35.0f, 1, CornerType.ChicaneR, true),
        new CircuitSegment(-1.2f, 80.0f, 2,0, true),
        new CircuitSegment(1.0f, 50.0f, 2, 0, false),
        new CircuitSegment(0.15f, 400.0f, 3, CornerType.NormalR, true),
        new CircuitSegment(0.0f, 300.0f, 3, 0, false),
            new CircuitSegment(0.0f, 10.0f, 0, 0, false),
            new CircuitSegment(0.0f, 10.0f, -1, 0, false),
        new CircuitSegment(-1.0f, 50.0f, 4, CornerType.ChicaneL, true),
        new CircuitSegment(1.0f, 50.0f, 4, CornerType.ChicaneL, true),
        new CircuitSegment(1.0f, 50.0f, 5,0, true),
        new CircuitSegment(-1.0f, 25.0f, 5,0, false),
        new CircuitSegment(0.0f, 200.0f, 5,0, false),
        new CircuitSegment(1.0f, 50.0f, 6, CornerType.NormalR, true),
        new CircuitSegment(0.0f, 200.0f, 6,0, false),
        new CircuitSegment(0.8f, 50.0f, 7, CornerType.NormalR, true),
        new CircuitSegment(0.0f, 200.0f, 7,0, false),
        new CircuitSegment(-0.1f,200.0f, 7,0, false),
        new CircuitSegment(0.0f, 400.0f, 7,0, false),
            new CircuitSegment(0.0f, 10.0f, 0,0, false),
            new CircuitSegment(0.0f, 10.0f, -2,0, false),
        new CircuitSegment(-0.8f, 70.0f, 8,CornerType.NormalL, true),
        new CircuitSegment(0.8f, 70.0f, 8,0, false),
        new CircuitSegment(0.3f, 150.0f, 9,CornerType.NormalR, true),
        new CircuitSegment(-0.3f, 75.0f, 9,0, false),
        new CircuitSegment(-1.0f, 80.0f, 10,CornerType.NormalL, true),
        new CircuitSegment(1.0f, 50.0f, 10, 0, false),
        new CircuitSegment(0.0f, 500.0f, 10, 0, false),
        new CircuitSegment(0.23f, 300.0f, 11, CornerType.NormalR, true),
        new CircuitSegment(-0.1f, 50.0f, 11, 0, false),
        new CircuitSegment(0.0f, 100.0f, 11, 0, false),
        new CircuitSegment(0.0f, 70.0f, 11, 0, false)
    ];
    private static float CalculateTrunRate(float lowSpeedThreshold, float highSpeedThreshold, float fElapsedTime, Car car)
    {
        if (car.Speed == 0.0f)
            return 0.15f * fElapsedTime;
        else if (car.Reverse)
            return (car.Speed / lowSpeedThreshold < 0.25f ? 0.25f : car.Speed / lowSpeedThreshold) * fElapsedTime;
        else if (car.Speed <= lowSpeedThreshold)
            return (car.Speed / lowSpeedThreshold < 0.15f ? 0.15f : car.Speed / lowSpeedThreshold) * fElapsedTime;
        else if (car.Speed <= highSpeedThreshold)
            return 1.0f * fElapsedTime;
        else
            return (1.0f - ((car.Speed - highSpeedThreshold) / (2.0f - highSpeedThreshold))) * fElapsedTime;
    }
}
class SectorManager
{
    public bool[] Sectors { get; private set; }
    public Stopwatch[] SectorTimers { get; private set; }
    public TimeSpan[] CurrentSectorTimes { get; private set; }
    public TimeSpan[] BestTimes { get; private set; }
    public string[] SectorColors { get; private set; }
    public int SectorDisplayDuration { get; set; } = 3000;

    public SectorManager()
    {
        Sectors = [true, true, true];
        SectorTimers =
        [
            new Stopwatch(),
            new Stopwatch(),
            new Stopwatch()
        ];
        CurrentSectorTimes = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        BestTimes = new TimeSpan[4]; // [lap best, sector1 best, sector2 best, sector3 best]
        // Default HUD colors (greyed out)
        SectorColors = ["#333333", "#333333", "#333333"];
    }

    /// <summary>
    /// Updates the sector times and HUD colors when a sector boundary is reached.
    /// </summary>
    /// <param name="cornerNumber">
    /// The corner indicator from the track. 
    /// For example, -1 for the end of sector 1, -2 for the end of sector 2.
    /// </param>
    /// <param name="lapTime">The current lap timer's elapsed time.</param>
    /// <param name="distance">The current distance traveled on the track.</param>
    /// <param name="trackDistance">The total length of the track.</param>
    public void UpdateSector(int cornerNumber, TimeSpan lapTime, float distance, float trackDistance)
    {
        // End of sector 1 (first sector)
        if (cornerNumber == -1 && Sectors[0])
        {
            if (lapTime < BestTimes[1] || BestTimes[1] == TimeSpan.Zero)
            {
                BestTimes[1] = lapTime;
                SectorColors[0] = "#44c540"; // Green for new best
            }
            else
            {
                SectorColors[0] = "#e8c204"; // Yellow for not improved
            }
            CurrentSectorTimes[0] = lapTime;
            SectorTimers[0].Restart();
            Sectors[0] = false;
        }
        // End of sector 2 (second sector)
        else if (cornerNumber == -2 && Sectors[1])
        {
            TimeSpan sectorTime = lapTime - BestTimes[1];
            if (sectorTime < BestTimes[2] || BestTimes[2] == TimeSpan.Zero)
            {
                BestTimes[2] = sectorTime;
                SectorColors[1] = "#44c540";
            }
            else
            {
                SectorColors[1] = "#e8c204";
            }
            CurrentSectorTimes[1] = lapTime;
            SectorTimers[1].Restart();
            Sectors[1] = false;
        }
        // End of sector 3 (when lap finishes)
        else if (distance >= trackDistance && Sectors[2])
        {
            TimeSpan sectorTime = lapTime - BestTimes[1] - BestTimes[2];
            if (sectorTime < BestTimes[3] || BestTimes[3] == TimeSpan.Zero)
            {
                BestTimes[3] = sectorTime;
                SectorColors[2] = "#44c540";
            }
            else
            {
                SectorColors[2] = "#e8c204";
            }
            CurrentSectorTimes[2] = lapTime;
            SectorTimers[2].Restart();
            Sectors[2] = false;
        }
    }

    /// <summary>
    /// Resets the sector states when a lap is completed.
    /// Also updates the lap best time if the current lap was faster.
    /// </summary>
    /// <param name="distance">Reference to the current distance traveled (will be reduced by trackDistance).</param>
    /// <param name="trackDistance">The total track length.</param>
    /// <param name="lapTimer">The stopwatch tracking the lap time.</param>
    public void ResetLap(ref float distance, float trackDistance, Stopwatch lapTimer)
    {
        distance -= trackDistance;
        // Reset sector flags for the new lap.
        Sectors[0] = true;
        Sectors[1] = true;
        Sectors[2] = true;
        // Update the lap best time if applicable.
        if (lapTimer.Elapsed < BestTimes[0] || BestTimes[0] == TimeSpan.Zero)
        {
            BestTimes[0] = lapTimer.Elapsed;
        }
        lapTimer.Restart();
    }

    /// Updates the HUD sector colors based on the elapsed time since a sector was recorded.
    /// If the sector display duration has passed, the colors are reset to the default.
    public void UpdateSectorHUD()
    {
        if (SectorTimers[2].ElapsedMilliseconds >= SectorDisplayDuration)
        {
            SectorColors[0] = "#333333";
            SectorColors[1] = "#333333";
            SectorColors[2] = "#333333";
            // Reset all timers.
            foreach (var timer in SectorTimers)
            {
                timer.Reset();
            }
        }
    }

    /// Determines which time should be displayed on the HUD.
    /// Returns the current sector's time if its display timer is active,
    /// otherwise returns the current lap time.
    public TimeSpan GetCurrentHudTime(Stopwatch lapTimer)
    {
        if (SectorTimers[0].IsRunning && SectorTimers[0].ElapsedMilliseconds > 0 && SectorTimers[0].ElapsedMilliseconds <= SectorDisplayDuration)
        {
            return CurrentSectorTimes[0];
        }
        else if (SectorTimers[1].IsRunning && SectorTimers[1].ElapsedMilliseconds > 0 && SectorTimers[1].ElapsedMilliseconds <= SectorDisplayDuration)
        {
            return CurrentSectorTimes[1];
        }
        else if (SectorTimers[2].IsRunning && SectorTimers[2].ElapsedMilliseconds > 0 && SectorTimers[2].ElapsedMilliseconds <= SectorDisplayDuration)
        {
            return CurrentSectorTimes[2];
        }
        else
        {
            return lapTimer.Elapsed;
        }
    }
}
class Renderer(int width, int height)
{
    private readonly int width = width;

    private readonly int height = height;

    private string[,] frame = new string[width, height];

    public static readonly string[][] CarSprite =
    [
        [
            "     00000       █████       00000     ",
            "     0□0□0███  █████████  ███0□0□0     ",
            "     □□□□█████████████████████□□□□     ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "0000000 ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ 0000000",
            "0□0□0□0▒◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘▒0□0□0□0",
            "□0□0□0□◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘□0□0□0□",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "      00000      █████        00000    ",
            "     0□0□0███  █████████  ███0□0□0     ",
            "    □□□□□█████████████████████□□□      ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "0000000 ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ 0000000",
            "0□0□0□0▒◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘▒0□0□0□0",
            "□0□0□0□◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘□0□0□0□",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "    00000        █████      00000      ",
            "     0□0□0███  █████████  ███0□0□0     ",
            "      □□□█████████████████████□□□□□    ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "0000000 ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ 0000000",
            "0□0□0□0▒◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘▒0□0□0□0",
            "□0□0□0□◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘□0□0□0□",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "     00000       █████       00000     ",
            "     □0□0□███  █████████  ███□0□0□     ",
            "     □□□□█████████████████████□□□□     ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "0000000 ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ 0000000",
            "□0□0□0□▒◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘▒□0□0□0□",
            "0□0□0□0◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘0□0□0□0",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "      00000      █████        00000    ",
            "     □0□0□███  █████████  ███□0□0□     ",
            "    □□□□□█████████████████████□□□      ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "0000000 ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ 0000000",
            "□0□0□0□▒◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘▒□0□0□0□",
            "0□0□0□0◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘0□0□0□0",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "    00000        █████      00000      ",
            "     □0□0□███  █████████  ███□0□0□     ",
            "      □□□█████████████████████□□□□□    ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "0000000 ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ 0000000",
            "□0□0□0□▒◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘▒□0□0□0□",
            "0□0□0□0◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘0□0□0□0",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ]
    ];
    public void Text(int x, int y, string text, string hex)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (x + i < width && y < height)
            {
                frame[x + i, y] += hex + text[i];
            }
        }
    }
    public void Text(int x, int y, string[] text, string hex)
    {
        for (int i = 0; i < text.Length; i++)
        {
            for (int n = 0; n < text[i].Length; n++)
            {
                if (x + n < width && y < height && text[i][n] != ' ')
                {
                    frame[x + n, y + i] += hex + text[i][n];
                }
            }
        }
    }
    public void Pixel(int startX, int startY, string hex)
    {
        if (startX < width && startY < height)
        {
            frame[startX, startY] = hex;
        }
    }
    public void DrawCar(int startX, int startY, string hex, int direction, float distance)
    {
        direction = (int)(distance / 10) % 2 == 0 ? direction : direction + 3;
        int spriteHeight = CarSprite[direction].Length;
        int spriteWidth = CarSprite[direction][0].Length;
        int frameX;

        for (int y = 0; y < spriteHeight; y++)
        {
            frameX = 0;
            for (int x = 0; x < spriteWidth; x++)
            {
                if (startX + x >= 0 && startX + x < width && startY + y >= 0 && startY + y < height && CarSprite[direction][y][x] != ' ')
                {
                    if (CarSprite[direction][y][x] == '□' && x < spriteWidth)
                        frame[startX + frameX, startY + y] = "#121212";
                    else if (CarSprite[direction][y][x] == '0' && x < spriteWidth)
                        frame[startX + frameX, startY + y] = "#1f1f1f";
                    else if (CarSprite[direction][y][x] == '▒' && x < spriteWidth)
                        frame[startX + frameX, startY + y] = "#222222";
                    else if (CarSprite[direction][y][x] == '◘' && x < spriteWidth)
                        frame[startX + frameX, startY + y] = "#ad0a2a";
                    else
                        frame[startX + frameX, startY + y] = hex;
                }
                if (frameX < spriteWidth)
                    frameX++;
            }
        }
    }
    public void ClearFrame()
    {
        frame = new string[width, height];
    }
    public void DisplayFrame()
    {
        StringBuilder stringBuilder = new();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if ((x > 0 && frame[x, y] == null && frame[x - 1, y] != null) || (x == 0 && frame[x, y] == null))
                    stringBuilder.Append("\x1b[48;2;65;120;200m" + ' ');
                else if (x > 0 && frame[x, y] == frame[x - 1, y] && frame[x, y] != null && (frame[x, y].Length == 7 || (frame[x, y].Length == 8 && frame[x, y][^1] == ' ')) || (frame[x, y] == null))
                    stringBuilder.Append(' ');
                else if (frame[x, y].Length > 8 && x > 0 && frame[x - 1, y] == null)
                    stringBuilder.Append(SetColor(frame[x, y]) + SetColorText(frame[x, y][7..]) + frame[x, y][^1]);
                else if (frame[x, y].Length == 8 && x > 0 && frame[x - 1, y] != null && frame[x - 1, y].Length == 8 && frame[x, y][..7] == frame[x - 1, y][..7])
                    stringBuilder.Append(frame[x, y][^1]);
                else if (frame[x, y].Length > 8 && x > 0 && frame[x - 1, y] != null && frame[x - 1, y].Length > 8 && frame[x, y][..14] == frame[x - 1, y][..14])
                    stringBuilder.Append(frame[x, y][^1]);
                else if (frame[x, y].Length > 8 && x > 0 && frame[x - 1, y] != null && frame[x - 1, y].Length > 8 && frame[x, y][7..14] == frame[x - 1, y][7..14])
                    stringBuilder.Append(SetColor(frame[x, y]) + frame[x, y][^1]);
                else if (frame[x, y].Length > 8 && x > 0 && frame[x - 1, y] != null && frame[x - 1, y].Length >= 7)
                    stringBuilder.Append(SetColor(frame[x, y]) + SetColorText(frame[x, y][7..]) + frame[x, y][^1]);
                else if (frame[x, y].Length > 8)
                    stringBuilder.Append(SetColorText(frame[x, y][7..]) + frame[x, y][^1]);
                else if (frame[x, y].Length == 8)
                    stringBuilder.Append(SetColorText(frame[x, y]) + frame[x, y][^1]);
                else if (frame[x, y].Length == 7 && x > 0 && frame[x - 1, y] == frame[x, y])
                    stringBuilder.Append(' ');
                else if (frame[x, y].Length == 7 && x > 0 && frame[x - 1, y] == null)
                    stringBuilder.Append(SetColor(frame[x, y]) + ' ');
                else
                    stringBuilder.Append(SetColor(frame[x, y]) + ' ');
            }
            if (y < height - 1)
                stringBuilder.AppendLine();
        }

        Console.SetCursorPosition(0, 0);
        Console.Write($"\x1b[48;2;65;120;200m" + stringBuilder);

        ClearFrame();
    }
    public static string SetColor(string hex) =>
        $"\x1b[48;2;{Convert.ToInt32(hex.Substring(1, 2), 16)};" +
                  $"{Convert.ToInt32(hex.Substring(3, 2), 16)};" +
                  $"{Convert.ToInt32(hex.Substring(5, 2), 16)}m";
    public static string SetColorText(string hex) =>
        $"\x1b[38;2;{Convert.ToInt32(hex.Substring(1, 2), 16)};" +
                  $"{Convert.ToInt32(hex.Substring(3, 2), 16)};" +
                  $"{Convert.ToInt32(hex.Substring(5, 2), 16)}m";
}
class TrackRenderer(Renderer renderer)
{
    private readonly Renderer _renderer = renderer;
    private readonly int screenHeight = Console.LargestWindowHeight;
    private readonly int screenHeightHalf = Console.LargestWindowHeight / 2;
    private readonly int screenWidth = Console.LargestWindowWidth;
    public void DrawTrack(float fCurvature, float fDistance, float fTrackDistance)
    {
        float fPerspective, fRoadWidth, fClipWidth, fMiddlePoint , fFinishLineDistance;
        int nLeftGrass, nLeftClip, nRightClip, nRightGrass, nRow;
        string nGrassColour, nClipColour;
        bool finishLine;

        for (int y = 0; y < screenHeightHalf; y++)
        {
            fPerspective = y / (screenHeight / 2.0f);
            fRoadWidth = 0.095f + fPerspective * 0.8f;
            fClipWidth = fRoadWidth * 0.125f;
            fRoadWidth *= 0.39f;

            fMiddlePoint = 0.5f + fCurvature * (float)Math.Pow(1.0f - fPerspective, 3.4);

            nLeftGrass = (int)((fMiddlePoint - fRoadWidth - fClipWidth) * screenWidth);
            nLeftClip = (int)((fMiddlePoint - fRoadWidth) * screenWidth);
            nRightClip = (int)((fMiddlePoint + fRoadWidth) * screenWidth);
            nRightGrass = (int)((fMiddlePoint + fRoadWidth + fClipWidth) * screenWidth);

            nRow = screenHeightHalf + y;

            fFinishLineDistance = fDistance + screenHeightHalf >= fTrackDistance ? fDistance + 12 - fTrackDistance: fDistance + 12;
            finishLine = Math.Abs((float)(fFinishLineDistance - y)) <= (float)(y / 30.0f + 0.5f);

            nGrassColour = (Math.Sin(18.0f * (float)Math.Pow(1.0f - fPerspective, 2.8) + fDistance * 0.1f) > 0.0f) ? "#22B14C" : "#15D653";
            nClipColour = (Math.Sin(50.0f * (float)Math.Pow(1.0f - fPerspective, 2) + fDistance) > 0.0f) ? "#FF3434" : "#F2F2F2";

            for (int x = 0; x < screenWidth; x++)
            {
                if (x >= 0 && x < nLeftGrass)
                    _renderer.Pixel(x, nRow, nGrassColour);
                else if (x >= nLeftGrass && x < nLeftClip)
                    _renderer.Pixel(x, nRow, nClipColour);
                else if (finishLine && x >= nLeftGrass && x < nRightClip)
                    _renderer.Pixel(x, nRow, ((x / 2) + y) % 2 == 0 ? "#202020" : "#F0F0F0");
                else if (x >= nLeftClip && x < nRightClip)
                    _renderer.Pixel(x, nRow, "#4D4D52");
                else if (x >= nRightClip && x < nRightGrass)
                    _renderer.Pixel(x, nRow, nClipColour);
                else if (x >= nRightGrass && x < screenWidth)
                    _renderer.Pixel(x, nRow, nGrassColour);
            }
        }
    }
}
class SceneryRenderer(Renderer renderer)
{
    private static readonly Random _random = new();
    private readonly Renderer _renderer = renderer;
    private readonly int screenHeightHalf = Console.LargestWindowHeight / 2;
    private readonly int screenWidth = Console.LargestWindowWidth;
    public void Particle(int x, int y, int radius, string color)
    {
        // Apply random movement for diffusion
        x += _random.Next(-1, 2); // Move randomly left, right, or stay
        y += _random.Next(-1, 2); // Move randomly up, down, or stay

        // Draw particle as a small circle
        for (int i = -radius; i <= radius; i++)
        {
            for (int j = -radius; j <= radius; j++)
            {
                if (i * i + j * j <= radius * radius) // Circle equation
                {
                    _renderer.Pixel(x + i, y + j, color);
                }
            }
        }
    }
    public void DrawScenery(float fTrackCurvature)
    {
        int nHillHeight, nBuildingHeight;

        for (int x = 0; x < screenWidth; x++)
        {
            // Draw scenery
            nHillHeight = (int)Math.Abs(Math.Sin(x * 0.02 + fTrackCurvature + 20) * 9.0);
            for (int h = screenHeightHalf - nHillHeight; h < screenHeightHalf; h++)
                _renderer.Pixel(x, h, "#2E6E41");

            // Draw buildings
            nBuildingHeight = (int)(Math.Sin(x * 0.05 + fTrackCurvature * 6) * 10) >= 9 ? 13 : 0;
            for (int h = screenHeightHalf - nBuildingHeight; h < screenHeightHalf; h++)
                _renderer.Pixel(x, h, "#575F66");

            nBuildingHeight = (int)(Math.Sin(x * 0.03 + fTrackCurvature * 7) * 10) >= 9 ? 8 : 0;
            for (int h = screenHeightHalf - nBuildingHeight; h < screenHeightHalf; h++)
                _renderer.Pixel(x, h, "#705356");

            nBuildingHeight = (int)(Math.Sin(x * 0.04 + fTrackCurvature * 9) * 10) >= 9 ? 5 : 0;
            for (int h = screenHeightHalf - nBuildingHeight; h < screenHeightHalf; h++)
                _renderer.Pixel(x, h, "#463C4D");
        }
    }
}
class HudRenderer(Renderer renderer)
{
    private readonly Renderer _renderer = renderer;
    public static string[][] HeaderGear =
    [
        [
            "╭──╮",
            "├─┬╯",
            "╵ ╰ "
        ],
        [
            "╭╮ ╷",
            "│╰╮│",
            "╵ ╰╯"
        ],
        [
            "  ╮ ",
            "  │ ",
            "  ┴ "
        ],
        [
            "  ╮ ",
            "  │ ",
            "  ┴ "
        ],
        [
            "  ╮ ",
            "  │ ",
            "  ┴ "
        ],
        [
            "  ╮ ",
            "  │ ",
            "  ┴ "
        ],
        [
            "  ╮ ",
            "  │ ",
            "  ┴ "
        ],
        [
            "╶──╮",
            "╭──╯",
            "╰──╴"
        ],
        [
            "╶──╮",
            " ──┤",
            "╶──╯"
        ],
        [
            "╷  ╷",
            "╰──┤",
            "   ╵"
        ],
        [
            "╭──╴",
            "╰──╮",
            "╶──╯"
        ],
        [
            "╭──╴",
            "├──╮",
            "╰──╯"
        ],
        [
            "╶──╮",
            "   │",
            "   ╵"
        ],
        [
            "╭──╮",
            "├──┤",
            "╰──╯"
        ],
        [
            "╭──╮",
            "├──┤",
            "╰──╯"
        ],
        [
            "╭──╮",
            "├──┤",
            "╰──╯"
        ],
        [
            "╭──╮",
            "├──┤",
            "╰──╯"
        ],
        [
            "╭──╮",
            "├──┤",
            "╰──╯"
        ]
    ];
    public static string[][] CornerTypeGraphic =
    [
        [
            "        ",
            "        ",
            "▀▄▀▄▀▄▀▄",
            "        ",
            "        "
        ],
        [
            "       ▲",
            "       │",
            "╭──────╯",
            "│       ",
            "╵       "
        ],
        [
            "▲       ",
            "│       ",
            "╰──────╮",
            "       │",
            "       ╵"
        ],
        [
            "╭──────╮",
            "│      │",
            "│      │",
            "│      ▼",
            "╵       "
        ],
        [
            "╭──────╮",
            "│      │",
            "│      │",
            "▼      │",
            "       ╵"
        ],
        [
            "╭──────►",
            "│       ",
            "│       ",
            "│       ",
            "╵       "
        ],
        [
            "◄──────╮",
            "       │",
            "       │",
            "       │",
            "       ╵"
        ]
    ];
    public static string[] hud =
    [
        "          ╭────────────────╮          ",
        "──────────╯                ╰──────────",
        "               ╮      ╭               ",
        "               │      │               ",
        "               ╯      ╰               "
    ];
    public static string[] times =
    [
        "     S1          SƧ          S3     ",
        " --/--           ",
        "▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄",
        " Best            "
    ];
    public static string[] CircuitSprite =
    [
        "╭─────╮                         ",
        "╰╮    ╰─╮                       ",
        " │      ╰─╮                     ",
        " ╰╮       ╰─╮                   ",
        "  │         ╰──╮                ",
        "  │            ╰───────────────╮",
        "  ╰─╮      ╭╮                ╭─╯",
        "    ╰──────╯╰────────────╌───╯  "
    ];
    public static readonly Dictionary<int, List<(int X, int Y)>> CircuitCoordinates = new()
    {
        { 1, new List<(int, int)> { (25, 7), (24, 7), (23, 7), (22, 7), (21, 7), (20, 7), (19, 7), (18, 7), (17, 7), (16, 7), (15, 7), (14, 7), (13, 7), (13, 7) } },
        { 2, new List<(int, int)> { (12, 7) } },
        { 3, new List<(int, int)> { (12, 6) } },
        { 4, new List<(int, int)> { (11, 6) } },
        { 5, new List<(int, int)> { (11, 7) } },
        { 6, new List<(int, int)> { (10, 7), (9, 7), (8, 7), (7, 7), (6, 7), (5, 7), (4, 7), (4, 6), (3, 6), (2, 6), (2, 6) } },
        { 7, new List<(int, int)> { (2, 5), (2, 4), (2, 4) } },
        { 8, new List<(int, int)> { (2, 4) } },
        { 9, new List<(int, int)> { (2, 4) } },
        { 10, new List<(int, int)> { (2, 3) } },
        { 11, new List<(int, int)> { (2, 3) } },
        { 12, new List<(int, int)> { (1, 3) } },
        { 13, new List<(int, int)> { (1, 3) } },
        { 14, new List<(int, int)> { (1, 2), (1, 1), (0, 1), (0, 1) } },
        { 15, new List<(int, int)> { (0, 0) } },
        { 16, new List<(int, int)> { (1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (5, 0) } },
        { 17, new List<(int, int)> { (6, 0) } },
        { 18, new List<(int, int)> { (6, 1), (7, 1), (8, 1), (8, 1) } },
        { 19, new List<(int, int)> { (8, 2), (9, 2), (10, 2), (10, 2) } },
        { 20, new List<(int, int)> { (10, 3), (11, 3), (12, 3), (12, 3) } },
        { 21, new List<(int, int)> { (12, 3) } },
        { 22, new List<(int, int)> { (12, 3) } },
        { 23, new List<(int, int)> { (12, 4) } },
        { 24, new List<(int, int)> { (12, 4) } },
        { 25, new List<(int, int)> { (13, 4), (14, 4), (15, 4) } },
        { 26, new List<(int, int)> { (15, 4) } },
        { 27, new List<(int, int)> { (15, 4) } },
        { 28, new List<(int, int)> { (15, 5) } },
        { 29, new List<(int, int)> { (16, 5), (17, 5), (18, 5), (19, 5), (20, 5), (21, 5), (22, 5), (23, 5), (24, 5), (25, 5), (26, 5), (27, 5), (28, 5), (29, 5), (30, 5), (30, 5) } },
        { 30, new List<(int, int)> { (31, 5), (31, 6), (30, 6), (29, 6), (29, 7) } },
        { 31, new List<(int, int)> { (29, 7) } },
        { 32, new List<(int, int)> { (28, 7), (27, 7), (26, 7), (26, 7) } },
    };
    public void UpdateShiftLights(Car car)
    {
        int preG, gear, carGear = car.NormalizeGear(car.Gear);

        (preG, gear) = carGear switch
        {
            0 => (0, 0),
            1 => (1, 6),
            8 => (12, 14),
            _ => (carGear + 4, carGear + 5)
        };

        float speedRatio = (car.Speed * 375 - Car.GearMaxSpeed[preG]) / (float)(Car.GearMaxSpeed[gear] - Car.GearMaxSpeed[preG]);
        int lightsOn = Math.Clamp((int)(15 * speedRatio), 0, 14);

        for (int i = 0; i < 14; i++)
        {
            string color = i switch
            {
                < 5 => "#c7fa93",
                < 9 => "#ff7a7a",
                _ => "#cc99ff"
            };
            _renderer.Text(
                Console.LargestWindowWidth / 2 - 7 + i,
                Console.LargestWindowHeight - 5,
                ".", i < lightsOn ? color : "#101317"
            );
        }
    }
    public void DrawNextCorner(int x, int y, string hex, float distanceBefore, CornerType cornerType)
    {
        if (distanceBefore < 100 && distanceBefore != 0)
            hex = (int)(distanceBefore / 10) % 2 == 0 ? "#BDBDBD" : hex;
        string[] sprite = CornerTypeGraphic[(int)cornerType];
        _renderer.Text(x, y, sprite, hex);
    }
    public void DrawCircuitMap(int x, int y, string hex, string hexIndicator, int distance, int section, int sectorDistance, int offset)
    {
        section = CircuitCoordinates.Keys.Where(k => k <= section).DefaultIfEmpty(1).Max();

        int index = Math.Clamp((int)(Math.Clamp((float)(distance - (offset - sectorDistance)) / sectorDistance, 0f, 1f) * 
            (CircuitCoordinates[section].Count - 1)), 0, CircuitCoordinates[section].Count - 1);

        (int coordX, int coordY) = CircuitCoordinates[section][index];

        string[] sprite = [.. CircuitSprite];

        if (coordY >= 0 && coordY < sprite.Length)
        {
            char[] row = sprite[coordY].ToCharArray();
            if (coordX >= 0 && coordX < row.Length)
                row[coordX] = ' ';
            sprite[coordY] = new string(row);
        }

        _renderer.Text(x, y, sprite, hex);
        _renderer.Text(x + coordX, y + coordY, "●", hexIndicator);
    }
    public void HudBuilder(Car car)
    {
        UpdateShiftLights(car);
        _renderer.Text(Console.LargestWindowWidth / 2 - 19, Console.LargestWindowHeight - 6, hud, "#101317");
        _renderer.Text(Console.LargestWindowWidth / 2 - 2, Console.LargestWindowHeight - 4, HeaderGear[car.Gear], "#FFFFFF");

        _renderer.Text(Console.LargestWindowWidth / 2 + 5, Console.LargestWindowHeight - 3, "км/ʜ", "#ababab");
        _renderer.Text(Console.LargestWindowWidth / 2 + 13 - ((int)Math.Abs(car.Speed * 375)).ToString().Length, Console.LargestWindowHeight - 3, ((int)Math.Abs(car.Speed * 375)).ToString(), "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth / 2 - 15,  Console.LargestWindowHeight - 3, "23%╺", "#F1C701");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 11, Console.LargestWindowHeight - 3, "#F1C701");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 10, Console.LargestWindowHeight - 3, "#F1C701");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 9,  Console.LargestWindowHeight - 3, "#8c7f3f");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 8,  Console.LargestWindowHeight - 3, "#8c7f3f");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 7,  Console.LargestWindowHeight - 3, "#8c7f3f");
        _renderer.Text(Console.LargestWindowWidth / 2 - 9, Console.LargestWindowHeight - 3, "Ϟ", "#000000");
    }
    public void TimesHud(TimeSpan time, TimeSpan bestTime)
    {
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 2, times[1], "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length + 16, 2, FormatTimeSpan(time), "#FFFFFF");
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 2, "#222222");

        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 3, "#222222");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 3, times[2], "#E8002D");

        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 4, "#222222");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length + 16, 4, bestTime.TotalMilliseconds == 0 ? "- ".PadLeft(19) : FormatTimeSpan(bestTime), "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 4, times[3], "#FFFFFF");
    }
    public void SectorsHud(string[] sectorColors)
    {
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 1, times[0], "#FFFFFF");
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 1, sectorColors[0]);
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length + 11, 1, sectorColors[1]);
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length + 23, 1, sectorColors[2]);
    }
    public static string FormatTimeSpan(TimeSpan time)
    {
        string result;

        // If time is less than 1 second
        if (time.TotalSeconds < 1)
        {
            result = time.TotalSeconds.ToString("0.### "); // "0.123"
        }
        // If time is less than 1 minute
        else if (time.TotalMinutes < 1)
        {
            result = time.Seconds.ToString("0") + "." + time.Milliseconds.ToString("000 "); // "45.123"
        }
        // If time is more than 1 minute
        else
        {
            result = time.Minutes.ToString("0") + ":" + time.Seconds.ToString("00") + "." + time.Milliseconds.ToString("000 "); // "1:05.211"
        }
        // Pad the string to a length of 19
        return result.PadLeft(19);
    }
}
class Car
{
    private static readonly double[] GearRatios = [0, 3.86, 2.94, 2.30, 1.91, 1.59, 1.34, 1.16, 1.00, 1.00];
    private const double FinalDriveRatio = 3.9;
    private const double MaxEngineRPM = 15000.0;
    private const double MinEngineRPM = 5000.0;

    public bool ClutchEngaged { get; set; } = true;
    public bool DRSEngaged { get; set; } = false;
    public bool Reverse { get; set; } = false;
    public int Gear { get; set; } = 1;
    public double RPM { get; private set; } = 7000.0;
    public float Speed { get; set; } = 0.0f;
    public float Acceleration { get; set; } = 0.00f;
    public float DRS => DRSEngaged ? 0.7f : 1.0f;
    public float Drag => 0.02f * Speed * DRS;

    public void UpdateRPM()
    {
        if (NormalizeGear(Gear) == 0 || NormalizeGear(Gear) > 8)
        {
            RPM = MinEngineRPM;
        }
        else
        {
            double mps = Math.Abs(Speed) / 3.6;
            double targetRPM = mps * 125 * FinalDriveRatio * GearRatios[NormalizeGear(Gear)] * 105;
            RPM = Math.Clamp(targetRPM, MinEngineRPM, MaxEngineRPM);
        }
        if (Math.Abs(Speed) > 0 && RPM == MinEngineRPM)
        {
            RPM = MinEngineRPM + Math.Abs(Speed);
        }
    }

    public int NormalizeGear(int gear)
    {
        if (gear == 0) return 1;
        if (gear == 1) return 0;
        if (gear <= 6) return 1;
        if (gear == 7) return 2;
        if (gear == 8) return 3;
        if (gear == 9) return 4;
        if (gear == 10) return 5;
        if (gear == 11) return 6;
        if (gear == 12) return 7;
        return 8;
    }

    public void Accelerate(float input, float fElapsedTime)
    {
        if (ClutchEngaged)
        {
            Speed += Reverse ? -((GearAccelerations[Gear] * input - Drag) * fElapsedTime)
                             : (GearAccelerations[Gear] * input - Drag) * fElapsedTime;
            Acceleration = (GearAccelerations[Gear] * input) - Drag;
        }
        else
        {
            Speed = 0.0001f;
        }
    }

    public void Decelerate(float input, float fElapsedTime)
    {
        Speed += Reverse ? 0.2f * input * fElapsedTime : -(0.2f * input * fElapsedTime);
        Acceleration = 0;
    }

    public void ShiftUp() => Gear = (Gear < GearAccelerations.Length - 1) && Gear > 0 ? Gear + 1 : Gear;
    public void ShiftDown() => Gear = Gear > 1 ? Gear - 1 : Gear;

    public void ReverseGear()
    {
        if (Gear == 1 && Speed <= 0.02f)
        {
            Reverse = true;
            Speed = 0;
            Gear--;
        }
        else if (Gear == 0 && -Speed <= 0.02f)
        {
            Reverse = false;
            Speed = 0;
            Gear++;
        }
    }

    public static readonly float[] GearAccelerations =
    [
        0.1f, 
        0.00001f, 
        0.01f, 0.02f, 0.03f, 0.15f, 0.28f,
        0.21f, 
        0.18f, 
        0.16f, 
        0.10f, 
        0.07f, 
        0.05f, 
        0.045f, 0.04f, 0.03f
    ];

    public static readonly float[] GearMaxSpeed =
    [
        -20.0f,
        0.00001f,
        0.01f, 2.0f, 11.0f, 20.0f, 110.0f,
        135.0f,
        170.0f,
        200.0f,
        230.0f,
        265.0f,
        300.0f,
        300.0f, 320.0f, 375.0f
    ];
}
class EngineSound
{
    private readonly WaveOutEvent waveOut;
    private readonly EngineWaveProvider waveProvider;
    private Thread? soundThread;
    private bool running = false;

    private double RPM = 5000;       
    private int gear = 1;                
    private double acceleration = 0;     

    public EngineSound()
    {
        waveProvider = new EngineWaveProvider(
            () => RPM,
            () => gear,
            () => acceleration);
        waveOut = new WaveOutEvent();
        waveOut.Init(waveProvider);
    }

    public void UpdateEngineState(double rpm, int currentGear, double accel)
    {
        RPM = rpm;
        gear = currentGear;
        acceleration = accel;
    }

    public void StartSound()
    {
        running = true;
        waveOut.Play();
        soundThread = new Thread(SoundUpdateLoop);
        soundThread.Start();
    }

    private void SoundUpdateLoop()
    {
        while (running)
        {
            Thread.Sleep(50);
        }
    }

    public void StopSound()
    {
        running = false;
        soundThread?.Join();
        waveOut.Stop();
        waveOut.Dispose();
    }
}
class EngineWaveProvider : WaveProvider32
{
    private readonly Func<double> getRPM;
    private readonly Func<int> getGear;
    private readonly Func<double> getAcceleration;
    private readonly int sampleRate = 44100;
    private double phase = 0;
    private const int NumHarmonics = 20;

    public EngineWaveProvider(Func<double> rpmFunc, Func<int> gearFunc, Func<double> accelerationFunc)
    {
        getRPM = rpmFunc;
        getGear = gearFunc;
        getAcceleration = accelerationFunc;
        SetWaveFormat(sampleRate, 1);
    }

    public override int Read(float[] buffer, int offset, int sampleCount)
    {
        double rpm = getRPM();
        int gear = getGear();
        double acceleration = getAcceleration();

        Random random = new();

        double baseFrequency = 30 + (rpm - 5000) / 1000.0 * 10;
        baseFrequency = Math.Clamp(baseFrequency, 30, 1000);

        double accelFactor = Math.Max(0, acceleration) / 510;
        double decelFactor = acceleration <= 0 ? 1 : 0;

        double gearFactor = gear switch
        {
            0 => 0.11,
            1 => 0.19,
            2 => 0.24,
            3 => 0.29,
            4 => 0.26,
            _ => 0.24
        };

        // Overall amplitude scaled by acceleration, deceleration, and gear.
        double amplitudeBase = Math.Clamp((0.5 + accelFactor - 0.3 * decelFactor) * gearFactor, 0.1, 1.2);
        double sampleValue;

        for (int i = 0; i < sampleCount; i++)
        {

            // MAIN ENGINE TONE: Sum weighted harmonics.
            double mainEngine = 0;
            for (int h = 1; h <= NumHarmonics; h++)
            {
                double weight = (h <= 3 ? 1.0 : (h <= 6 ? 0.6 : 0.3)) * gearFactor;
                if (accelFactor > 0 && h > 3)
                    weight *= (1 + 1.25 * accelFactor);
                if (decelFactor > 0 && h > 3)
                    weight /= (1 + decelFactor * 5);
                mainEngine += weight * Math.Sin(phase * h);
            }
            mainEngine = (mainEngine / NumHarmonics) * amplitudeBase;

            // WHINE: High-frequency modulation adds an edge.
            double whine = 0.2 * gearFactor * Math.Sin(phase * 10 * (1 + accelFactor))
                         * Math.Sin(phase * 0.3 * (1 + decelFactor));

            // POWER STROKE: Occasional burst to simulate combustion dynamics.
            double powerStroke = (Math.Sin(phase * 0.5 * gearFactor) * Math.Sin(phase * 3.2 * accelFactor))
                               * (random.NextDouble() > 0.95 ? 0.5 * gearFactor : 0);

            // VIBRATION: High-frequency micro-modulations simulating physical vibrations.
            double vibration = 0.1 * gearFactor * Math.Sin(phase * 25) * Math.Sin(phase * 0.8);

            // MECHANICAL NOISE: Random gritty noise.
            double mechanicalNoise = (random.NextDouble() - 0.5) * 0.05 * gearFactor;

            // VISERAL GROWL: Low-pitched, throaty layer using non-linear saturation.
            double growl = 0;
            if (accelFactor > 0.3)
            {
                double rawGrowl = Math.Sin(phase * 0.7 + Math.Sin(phase * 0.3));
                growl = Math.Tanh(rawGrowl * 3) * 0.4 * (accelFactor - 0.3) * gearFactor;
            }

            // Sum all layers to get the final sample value.
            sampleValue = mainEngine + whine + powerStroke + vibration
                          + mechanicalNoise + growl;

            // Apply a simple low-pass filter by blending with the previous sample.
            double previousSample = i > 0 ? buffer[offset + i - 1] : 0;
            sampleValue = sampleValue * 0.85 + previousSample * 0.15;

            buffer[offset + i] = (float)sampleValue;

            // Advance global phase based on the base frequency.
            double globalPhaseIncrement = 2 * Math.PI * baseFrequency / sampleRate;
            phase += globalPhaseIncrement;
            if (phase > 2 * Math.PI)
                phase -= 2 * Math.PI;
        }
        return sampleCount;
    }
}
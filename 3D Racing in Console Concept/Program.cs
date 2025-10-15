using NAudio.Wave;
using RacingConsole.Models;
using SharpDX.XInput;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.UI.Input.Inking;
using Windows.UI.Xaml.Controls.Maps;
using static Car;
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

public static class XInputGamepad
{
    private static readonly Controller _controller = new(UserIndex.One);
    private static Gamepad State => _controller.GetState().Gamepad;

    public static bool IsConnected => _controller.IsConnected;

    public static float GetLeftThumbX()
    {
        if (!IsConnected) return 0f;
        int raw = State.LeftThumbX;
        int dz = Gamepad.LeftThumbDeadZone;
        if (raw > -dz && raw < dz) return 0f;
        return raw < 0 ? raw / 32768f : raw / 32767f;
    }
    public static void SetVibration(float leftMotor, float rightMotor)
    {
        if (!IsConnected) return;

        var vibration = new Vibration
        {
            LeftMotorSpeed = (ushort)(Math.Clamp(leftMotor, 0f, 1f) * ushort.MaxValue),
            RightMotorSpeed = (ushort)(Math.Clamp(rightMotor, 0f, 1f) * ushort.MaxValue)
        };

        _controller.SetVibration(vibration);
    }

    public static void StopVibration()
    {
        SetVibration(0f, 0f);
    }

    public static float GetThrottle()
        => IsConnected ? State.RightTrigger / 255f : 0f;

    public static float GetBrake()
        => IsConnected ? State.LeftTrigger / 255f : 0f;

    public static bool IsButtonDown(GamepadButtonFlags btn)
        => IsConnected && (State.Buttons & btn) == btn;
}

public partial class Racing
{
    #region Prerequisites
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
    const int STD_INPUT_HANDLE = -10;
    const uint ENABLE_QUICK_EDIT = 0x0040;
    const uint ENABLE_EXTENDED_FLAGS = 0x0080;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    private const int VK_F11 = 0x7A;

    private const uint WM_KEYDOWN = 0x100;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static void SetConsoleFullscreen()
    {
        var hwnd = GetConsoleWindow();
        PostMessage(hwnd, WM_KEYDOWN, VK_F11, IntPtr.Zero);
    }

    private static void SetConsoleBufferSize()
    {
        int nWidth = Console.LargestWindowWidth, nHeight = Console.LargestWindowHeight;
        Console.SetWindowSize(nWidth, nHeight);
        Console.SetBufferSize(nWidth, nHeight);
        Console.SetWindowSize(nWidth, nHeight);
    }

    static void DisableQuickEditMode()
    {
        IntPtr consoleHandle = GetStdHandle(STD_INPUT_HANDLE);
        if (!GetConsoleMode(consoleHandle, out uint mode))
            return;

        mode &= ~ENABLE_QUICK_EDIT;
        mode |= ENABLE_EXTENDED_FLAGS;

        SetConsoleMode(consoleHandle, mode);
    }
    #endregion

    private static readonly JsonSerializerOptions CachedJsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };
    private static bool inPit = false;
    private static readonly float pitEntryDistance = 100f;
    private static readonly float PitLaneLength = 300f;
    static void Main()
    {
        Console.OutputEncoding = Encoding.Unicode;
        CustomColor.Color();
        Console.Write("\x1b[48;2;35;35;35m");
        SetConsoleFullscreen();
        Thread.Sleep(1000);
        SetConsoleBufferSize();
        DisableQuickEditMode();
        Thread.Sleep(1000);
        int width = Console.LargestWindowWidth, 
            height = Console.LargestWindowHeight;
        Console.CursorVisible = false;

        string teamsJson = File.ReadAllText("Teams.json");
        var teams = JsonSerializer.Deserialize<List<Team>>(teamsJson);
        var otherCars = teams!
          .SelectMany(team => team.Drivers, (team, driver) => new { team, driver })
          .Select((td, idx) =>
          {
              float trackOffset = idx * 8f;
              float xOffset = (idx % 2 == 0) ? -0.05f : 0.05f;
              return new CPUCar(td.team, td.driver, xOffset, trackOffset);
          })
          .ToList();

        CachedJsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        CachedJsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());

        string grandPrixJson = File.ReadAllText("GrandPrix.json");
        List<GrandPrix> gps = JsonSerializer.Deserialize<List<GrandPrix>>(grandPrixJson, CachedJsonSerializerOptions) ?? [];
        GrandPrix gp = gps[0];

        float trackLength = 0.0f;

        const float MaxAudibleDistance = 100f;
        const float MaxSideOffset = 2f;
        const float LateralWeight = 0.5f;

        long eFrameTime = 0;
        const int targetFps = 40,
                  frameTime = 1000 / targetFps;
        const float eTime = (float)frameTime / 1000;

        int playerCarY = height - 15;
        int classificationType = 1;
        bool small = true;

        var circuitSegments = gp.Circuit.Segments;
        foreach (var t in circuitSegments)
            trackLength += t.Length;

        Car carPlayer = new();

        CornerType nextCornertype;

        Stopwatch frameStopwatch = new();
        Stopwatch keyToggleDelay = new();
        Stopwatch lapTime = new();
        Stopwatch totalTime = new();
        SectorManager sectorManager = new();
        EngineSound engineSound = new();
        var cpuCarEngineSounds = otherCars
            .Select(_ => {
                var sound = new EngineSound();
                return sound;
            })
            .ToList();

        Renderer renderer = new(width, height);

        TrackRenderer trackRenderer = new(renderer, gp.Track);
        SceneryRenderer sceneryRenderer = new(renderer, gp.Scenery);
        HudRenderer hudRenderer = new(renderer);
        CarRenderer carRenderer = new(renderer);

        var userCarColorBase = "#20b4f5";
        var userCarColorShade = "#22749c";
        var userCarColorParts = "#181b1c";
        var userCarColorAccent = "#f17311";

        renderer.Rectangle(0, 12, 52, 1, "#232323");
        renderer.Text(40, 12, "PRESS ENTER", "#FFFFFF");
        carRenderer.DrawPlayerCar(width / 2 - 20, playerCarY, 0, 0, userCarColorBase, userCarColorShade, userCarColorParts, userCarColorAccent);
        renderer.Render("#0C0C0C");

        while (true)
            if (Keyboard.IsKeyPressed(ConsoleKey.Enter) || XInputGamepad.IsButtonDown(GamepadButtonFlags.Start))
                break;
        
        totalTime.Start();
        lapTime.Start();
        keyToggleDelay.Start();
        frameStopwatch.Start();

        engineSound.StartSound();
        bool enterPits = false;
        cpuCarEngineSounds.ForEach(sound => sound.StartSound());
        while (true)
        {
            frameStopwatch.Restart();

            otherCars.ForEach(car => car.Update(eTime, circuitSegments, trackLength, circuitSegments[car.TrackSection].Section));

            #region Sound
            carPlayer.UpdateRPM();
            foreach (var pair in otherCars.Zip(cpuCarEngineSounds, (car, sound) => new { car, sound }))
            {
                pair.car.UpdateRPM();

                float distanceDiff = MathF.Abs(carPlayer.Distance + 27f - pair.car.Distance);

                float sideOffset = pair.car.PositionX - carPlayer.PositionX;
                float lateralDiff = MathF.Abs(sideOffset);

                float totalDist = MathF.Sqrt(distanceDiff * distanceDiff + lateralDiff * LateralWeight * (lateralDiff * LateralWeight));

                float proximity = Math.Clamp(1f - (totalDist / MaxAudibleDistance), 0f, 1f);

                if (proximity <= 0f)
                {
                    pair.sound.PauseSound();
                    continue;
                }
                else if (pair.sound.Pause && proximity > 0f)
                    pair.sound.UnpauseSound();

                float pan = 0.5f + Math.Clamp(sideOffset / MaxSideOffset, -0.5f, 0.5f) * (0.5f * proximity);

                pair.sound.UpdateEngineState(pair.car.RPM, pair.car.NormalizedGear, pair.car.Acceleration, proximity, pan);
            }
            engineSound.UpdateEngineState(carPlayer.RPM, carPlayer.NormalizedGear, carPlayer.Acceleration);
            #endregion

            carPlayer.Direction = 0;
            HandleInput(ref classificationType, ref small, ref enterPits, carPlayer, eTime, carPlayer.Distance, gp.Circuit.DRSZones, keyToggleDelay);

            const float TrackEdge = 0.59f;

            if (!inPit)
            {
                float outerOffset = carPlayer.PositionX;
                float innerOffset = carPlayer.PositionX - carPlayer.TotalCurvature;

                bool outerOff = outerOffset * carPlayer.TotalCurvature > 0f && Math.Abs(outerOffset) > TrackEdge + 0.01f;

                bool innerOff = Math.Abs(innerOffset) > TrackEdge && !carPlayer.Reverse;

                bool offTrackLeft = (outerOff && outerOffset < 0f) || (innerOff && innerOffset < 0f);
                bool offTrackRight = (outerOff && outerOffset > 0f) || (innerOff && innerOffset > 0f);
                if (offTrackLeft || offTrackRight)
                {
                    float penaltyMultiplier = Math.Abs(carPlayer.TotalCurvature * 5 * carPlayer.Speed * (carPlayer.Gear / 5f) + 1f);
                    float speedAdjustment = (carPlayer.Reverse ? 1f : -1f) * penaltyMultiplier * eTime;
                    carPlayer.Speed += speedAdjustment;
                }

                if (XInputGamepad.IsConnected)
                {
                    float rumbleStrength = MathF.Min(carPlayer.Speed, 1f);

                    if (offTrackLeft)
                        XInputGamepad.SetVibration(rumbleStrength, 0f);
                    else if (offTrackRight)
                        XInputGamepad.SetVibration(0f, rumbleStrength);
                    else
                        XInputGamepad.StopVibration();
                }
                else
                {
                    XInputGamepad.StopVibration();
                }
            }

            carPlayer.UpdateSpeed(eTime);

            float offset = -1;
            carPlayer.TrackSection = -1;
            for (; carPlayer.TrackSection < circuitSegments.Count - 1 && offset < carPlayer.Distance; carPlayer.TrackSection++)
                offset += circuitSegments[carPlayer.TrackSection + 1].Length;

            sectorManager.UpdateSector(circuitSegments[carPlayer.TrackSection].Section, lapTime.Elapsed, carPlayer.Distance, trackLength, [.. otherCars.Select(car => car.BestTimes)]);
            sectorManager.UpdateSectorHUD();
            if (carPlayer.HandleFinishLineCrossing(trackLength, lapTime.Elapsed.TotalSeconds))
                sectorManager.ResetLap(lapTime);

            (int cornerNumber, float distanceToNextCorner, nextCornertype) = CheckNextCorner(carPlayer.Distance, circuitSegments);

            carPlayer.UpdateCurvature(circuitSegments[carPlayer.TrackSection].Curvature, eTime);

            float pitEnterProgress = 0;
            if (enterPits && !inPit)
                pitEnterProgress = Math.Clamp((carPlayer.Distance - pitEntryDistance) / 100f, 0f, 1f);
            if (pitEnterProgress == 1f)
                inPit = true;

            if (inPit)
            {
                enterPits = false;
                carPlayer.PositionX = 0.0f;
                carPlayer.Speed = Math.Min(carPlayer.Speed, 0.2f);

                float pitProgress = (carPlayer.Distance - pitEntryDistance) / PitLaneLength;

                if (pitProgress >= 1f)
                {
                    inPit = false;
                    carPlayer.PositionX = -0.5f;
                }
            }
            else
            {
                carPlayer.PositionX = carPlayer.Curvature - carPlayer.TrackCurvature * 9f;
            }

            var roadBoundaries = new List<(int Left, int Right)>();

            if (!inPit)
                roadBoundaries = trackRenderer.DrawTrack(carPlayer.TotalCurvature, carPlayer.Distance, trackLength, pitEnterProgress, enterPits);
            else
                roadBoundaries = trackRenderer.DrawPits(carPlayer.TotalCurvature, carPlayer.Distance, trackLength, pitEnterProgress, enterPits);

            int playerCarX = width / 2 + ((int)(width * carPlayer.PositionX) / 2) - 20;

            var list = otherCars.Select(c => (c.Distance, c.LapsCompleted, (CPUCar?)c, false))
                .Append((carPlayer.Distance, carPlayer.LapsCompleted, null, true))
                .OrderByDescending(e => e.LapsCompleted)
                .ThenByDescending(e => e.Distance)
                .Select((e, idx) => (e.Distance, e.Item3, e.Item4, idx))
                .ToList();
            list = [.. list.OrderByDescending(e => e.Distance)];

            foreach (var (dist, car, isPlayer, idx) in list)
                if (isPlayer)
                    carRenderer.DrawPlayerCar(playerCarX, playerCarY, carPlayer.Distance, carPlayer.Direction, userCarColorBase, userCarColorShade, userCarColorParts, userCarColorAccent);
                else
                    carRenderer.DrawCPUCar(car!, carPlayer.Distance, trackLength, roadBoundaries, idx);

            sceneryRenderer.DrawScenery(carPlayer.TrackCurvature);
            sceneryRenderer.DrawTrees(carPlayer.TotalCurvature, carPlayer.Distance);
            if (inPit)
                sceneryRenderer.DrawWalls(carPlayer.TotalCurvature, carPlayer.Distance);

            hudRenderer.DrawCircuitMap(width / 2 - gp.Circuit.CircuitMap[0].Length / 2, 1, "#FFFFFF", userCarColorBase, (int)carPlayer.Distance, carPlayer.TrackSection, (int)circuitSegments[carPlayer.TrackSection].Length, (int)offset, gp.Circuit.CircuitMap);
            hudRenderer.HudBuilder(carPlayer, gp.Circuit.DRSZones, carPlayer.Distance, trackLength);
            hudRenderer.DrawNextCorner(width / 2 + HudRenderer._cornerIndicatorWidth + 24, 3, "#FFFFFF", distanceToNextCorner, cornerNumber, nextCornertype);
            hudRenderer.TimesHud(2, 1, sectorManager.SectorColors, sectorManager.GetSectorTimesHUD(lapTime), sectorManager.BestTimes[0]);
            hudRenderer.ClassificationHud(trackLength, otherCars, sectorManager.BestTimes[0], carPlayer.TotalCoveredDistance, 1, classificationType, totalTime.Elapsed, carPlayer.LapsCompleted, carPlayer.LastLapTime, small);
            hudRenderer.DrawCarinfo(width - 25, 35, "#333333", "#287336", "#1c1c1c", "#222222", totalTime.ElapsedMilliseconds);

            renderer.Render(gp.Scenery.SkyColor);

            eFrameTime = frameStopwatch.ElapsedMilliseconds;
            if (eFrameTime <= frameTime)
                Thread.Sleep((int)(frameTime - eFrameTime));
        }
    }

    public static (int cornerNumber, float distanceToNextCorner, CornerType) CheckNextCorner(float distance, List<CircuitSegment> circuitSegments)
    {
        float distanceCovered = 0.0f;

        foreach (var segment in circuitSegments)
        {
            distanceCovered += segment.Length;

            if (distanceCovered > distance && segment.CornerType != 0)
            {
                float distanceToNextCorner = distanceCovered - distance;
                return (segment.Section, distanceToNextCorner, segment.CornerType);
            }
        }
        return (0, 0.0f, 0);
    }

    static void HandleInput(ref int classificationType, ref bool small, ref bool enterPits, Car playerCar, float eTime, float distance, List<DRSZone> drsZones, Stopwatch keyDelay)
    {
        bool gp = XInputGamepad.IsConnected;
        float steer = gp
                          ? XInputGamepad.GetLeftThumbX()
                          : 0f;
        float throttle = gp
                          ? XInputGamepad.GetThrottle()
                          : 0f;
        float brake = gp
                          ? XInputGamepad.GetBrake()
                          : 0f;

        if (steer != 0f)
            playerCar.Turn(-steer, eTime);
        else if (Keyboard.IsKeyPressed(ConsoleKey.A))
            playerCar.Turn(1f, eTime);
        else if (Keyboard.IsKeyPressed(ConsoleKey.D))
            playerCar.Turn(-1f, eTime);
        if (throttle > 0f)
        {
            playerCar.Accelerate(throttle, eTime);
            playerCar.ChargeERS(throttle * 0.2f, eTime);
        }
        else if (Keyboard.IsKeyPressed(ConsoleKey.W))
        {
            playerCar.Accelerate(1.0f, eTime);
            playerCar.ChargeERS(0.2f, eTime);
        }
        else if (brake > 0f)
        {
            playerCar.Decelerate(brake * 3.0f, eTime);
            playerCar.ChargeERS(brake * 1.5f, eTime);
        }
        else if (Keyboard.IsKeyPressed(ConsoleKey.S))
        {
            playerCar.Decelerate(3.0f, eTime);
            playerCar.ChargeERS(1.5f, eTime);
        }
        else
        {
            playerCar.Decelerate(0.45f, eTime);
        }
        bool inDRS = drsZones.Any(z => distance >= z.Start && distance <= z.End);
        if (inDRS && ((gp && XInputGamepad.IsButtonDown(GamepadButtonFlags.RightShoulder)) || Keyboard.IsKeyPressed(ConsoleKey.Spacebar)) && keyDelay.ElapsedMilliseconds > 350)
        {
            playerCar.DRSEngaged = !playerCar.DRSEngaged;
            keyDelay.Restart();
        }
        if (!inDRS) playerCar.DRSEngaged = false;
        if ((gp && XInputGamepad.IsButtonDown(GamepadButtonFlags.LeftShoulder) || Keyboard.IsKeyPressed(ConsoleKey.R)) && keyDelay.ElapsedMilliseconds > 350)
        {
            playerCar.ReverseGear();
            keyDelay.Restart();
        }
        if (gp)
        {
            if (XInputGamepad.IsButtonDown(GamepadButtonFlags.A))
                playerCar.ERSState = ERSStates.Attack;
            else if (XInputGamepad.IsButtonDown(GamepadButtonFlags.B))
                playerCar.ERSState = ERSStates.Balanced;
            else if (XInputGamepad.IsButtonDown(GamepadButtonFlags.X))
                playerCar.ERSState = ERSStates.Harvest;
            else if (XInputGamepad.IsButtonDown(GamepadButtonFlags.Y))
                playerCar.ERSState = ERSStates.Off;
        }
        else
        {
            if (Keyboard.IsKeyPressed(ConsoleKey.NumPad0)) playerCar.ERSState = ERSStates.Off;
            if (Keyboard.IsKeyPressed(ConsoleKey.NumPad1)) playerCar.ERSState = ERSStates.Harvest;
            if (Keyboard.IsKeyPressed(ConsoleKey.NumPad2)) playerCar.ERSState = ERSStates.Balanced;
            if (Keyboard.IsKeyPressed(ConsoleKey.NumPad3)) playerCar.ERSState = ERSStates.Attack;
        }

        if ((gp && XInputGamepad.IsButtonDown(GamepadButtonFlags.Start)) || Keyboard.IsKeyPressed(ConsoleKey.M))
        {
            if (keyDelay.ElapsedMilliseconds > 300)
            {
                classificationType = (classificationType + 1) % 3;
                keyDelay.Restart();
            }
        }
        if ((gp && XInputGamepad.IsButtonDown(GamepadButtonFlags.Back)) || Keyboard.IsKeyPressed(ConsoleKey.Tab))
        {
            if (keyDelay.ElapsedMilliseconds > 300)
            {
                small = !small;
                keyDelay.Restart();
            }
        }
        if (((gp && XInputGamepad.IsButtonDown(GamepadButtonFlags.DPadDown)) || Keyboard.IsKeyPressed(ConsoleKey.P)) && distance < pitEntryDistance && distance > pitEntryDistance - 100f)
        {
            if (keyDelay.ElapsedMilliseconds > 300)
            {
                enterPits = !enterPits;
                keyDelay.Restart();
            }
        }
        if (playerCar.Speed >= GearMaxSpeed[playerCar.Gear] / 375f)
            playerCar.ShiftUp();
        else if (playerCar.Gear > 0 && playerCar.Speed <= GearMaxSpeed[playerCar.Gear - 1] / 375f)
            playerCar.ShiftDown();
    }
}



class SectorManager
{
    public bool[] SectorDone { get; private set; }
    public string[] SectorColors { get; private set; }
    public TimeSpan[] CurrentSectorTimes { get; private set; }
    public TimeSpan[] BestTimes { get; private set; }
    public Stopwatch SectorInfoBufferTimer { get; private set; }


    public const int SectorInfoBufferDuration = 2500;

    public const string DefaultSector = "#333333";
    public const string GreenSector = "#44c540";
    public const string YellowSector = "#e8c204";
    public const string PurpleSector = "#a020f0";

    public SectorManager()
    {
        SectorDone = [false, false, false];
        SectorColors = [DefaultSector, DefaultSector, DefaultSector];
        SectorInfoBufferTimer = new Stopwatch();
        CurrentSectorTimes = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
        BestTimes = new TimeSpan[4];
    }

    public void UpdateSector(int cornerNumber, TimeSpan lapTime, float distance, float trackDistance, TimeSpan[][] bestTimes)
    {
        if (cornerNumber == -1 && !SectorDone[0])
        {
            if (lapTime < BestTimes[1] || BestTimes[1] == TimeSpan.Zero)
            {
                BestTimes[1] = lapTime;
                bool isFastestOfAll = true;
                for (int i = 0; i < bestTimes.Length; i++)
                {
                    TimeSpan cpuTime = bestTimes[i][1];
                    if (cpuTime != TimeSpan.Zero && lapTime >= cpuTime)
                    {
                        isFastestOfAll = false;
                        break;
                    }
                }
                SectorColors[0] = isFastestOfAll ? PurpleSector : GreenSector;
            }
            else
                SectorColors[0] = YellowSector;

            CurrentSectorTimes[0] = lapTime;
            SectorInfoBufferTimer.Restart();
            SectorDone[0] = true;
        }
        else if (cornerNumber == -2 && !SectorDone[1] && BestTimes[1] != TimeSpan.Zero)
        {
            TimeSpan sectorTime = lapTime - CurrentSectorTimes[0];
            if (sectorTime < BestTimes[2] || BestTimes[2] == TimeSpan.Zero)
            {
                BestTimes[2] = sectorTime;
                bool isFastestOfAll = true;
                for (int i = 0; i < bestTimes.Length; i++)
                {
                    TimeSpan cpuTime = bestTimes[i][2];
                    if (cpuTime != TimeSpan.Zero && sectorTime >= cpuTime)
                    {
                        isFastestOfAll = false;
                        break;
                    }
                }
                SectorColors[1] = isFastestOfAll ? PurpleSector : GreenSector;
            }
            else
            {
                SectorColors[1] = YellowSector;
            }
            CurrentSectorTimes[1] = sectorTime;
            SectorInfoBufferTimer.Restart();
            SectorDone[1] = true;
        }
        else if (distance >= trackDistance && !SectorDone[2] && BestTimes[1] != TimeSpan.Zero && BestTimes[2] != TimeSpan.Zero)
        {
            TimeSpan sectorTime = lapTime - CurrentSectorTimes[0] - CurrentSectorTimes[1];
            if (sectorTime < BestTimes[3] || BestTimes[3] == TimeSpan.Zero)
            {
                BestTimes[3] = sectorTime;
                bool isFastestOfAll = true;
                for (int i = 0; i < bestTimes.Length; i++)
                {
                    TimeSpan cpuTime = bestTimes[i][3];
                    if (cpuTime != TimeSpan.Zero && sectorTime >= cpuTime)
                    {
                        isFastestOfAll = false;
                        break;
                    }
                }
                SectorColors[2] = isFastestOfAll ? PurpleSector : GreenSector;
            }
            else
                SectorColors[2] = YellowSector;
            CurrentSectorTimes[2] = sectorTime;
            SectorInfoBufferTimer.Restart();
            SectorDone[2] = true;
        }
        CheckPurpleSectorsAgainstCpu(bestTimes);
    }

    public void ResetLap(Stopwatch lapTimer)
    {
        if ((lapTimer.Elapsed < BestTimes[0] || BestTimes[0] == TimeSpan.Zero) && BestTimes[1] != TimeSpan.Zero && BestTimes[2] != TimeSpan.Zero && BestTimes[3] != TimeSpan.Zero)
            BestTimes[0] = CurrentSectorTimes[0] + CurrentSectorTimes[1] + CurrentSectorTimes[2];
        SectorDone = [false, false, false];
        lapTimer.Restart();
    }

    public void UpdateSectorHUD()
    {
        if (SectorInfoBufferTimer.ElapsedMilliseconds >= SectorInfoBufferDuration && !SectorDone[0] && !SectorDone[1] && !SectorDone[2])
        {
            SectorColors[0] = DefaultSector;
            SectorColors[1] = DefaultSector;
            SectorColors[2] = DefaultSector;
            SectorInfoBufferTimer.Reset();
        }
    }

    public (TimeSpan, bool, byte) GetSectorTimesHUD(Stopwatch lapTimer)
    {
        if (SectorInfoBufferTimer.IsRunning && SectorInfoBufferTimer.ElapsedMilliseconds >= 0 && SectorInfoBufferTimer.ElapsedMilliseconds <= SectorInfoBufferDuration)
        {
            if (SectorDone[0] && !SectorDone[1] && !SectorDone[2])
                return (CurrentSectorTimes[0], true, 0);
            else if (SectorDone[0] && SectorDone[1] && !SectorDone[2])
                return (CurrentSectorTimes[1], true, 1);
            else
                return (CurrentSectorTimes[2], true, 2);
        }
        else
            return (lapTimer.Elapsed, false, 3);
    }

    public void CheckPurpleSectorsAgainstCpu(TimeSpan[][] cpuTimes)
    {
        if (cpuTimes == null || cpuTimes.Length == 0)
            return;
        for (int sector = 1; sector <= 3; sector++)
        {
            if (SectorColors[sector - 1] != PurpleSector)
                continue;
            if (BestTimes[sector] == TimeSpan.Zero)
                continue;
            foreach (TimeSpan[] cpuSectorTimes in cpuTimes)
            {
                if (sector >= cpuSectorTimes.Length)
                    continue;
                if (cpuSectorTimes[sector] == TimeSpan.Zero || cpuSectorTimes[sector] >= BestTimes[sector])
                    continue;
                SectorColors[sector - 1] = GreenSector;
                break;
            }
        }
    }
}


class Renderer
{
    public readonly int width;
    public readonly int height;

    private readonly StringBuilder[,] frame;

    public Renderer(int width, int height)
    {
        this.width = width;
        this.height = height;
        frame = new StringBuilder[width, height];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                frame[x, y] = new StringBuilder(13);
    }

    public void Text(int x, int y, char c, string color)
    {
        Char(x, y, c, color);
    }

    public void Text(int x, int y, string text, string color)
    {
        for (int i = 0; i < text.Length; i++)
            Char(x + i, y, text[i], color);
    }

    public void Text(int x, int y, string[] lines, string color)
    {
        for (int row = 0; row < lines.Length; row++)
            for (int i = 0; i < lines[row].Length; i++)
                if (lines[row][i] != ' ' && x + i >= 0 && x + i < width)
                    Char(x + i, y + row, lines[row][i], color);
    }

    public void Pixel(int x, int y, string color)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
        {
            frame[x, y].Clear();
            frame[x, y].Append(color);
        }
    }
    public void Shadow(int x, int y)
    {
        if (x < 0 || x >= width || y < 0 || y >= height)
            return;

        var sb = frame[x, y];
        string content = sb.ToString();

        if (content.Length == 8 || content.Length < 7)
            return;

        string hex = content[..7];

        int r = Convert.ToInt32(hex.Substring(1, 2), 16);
        int g = Convert.ToInt32(hex.Substring(3, 2), 16);
        int b = Convert.ToInt32(hex.Substring(5, 2), 16);

        r = (int)(r * 0.7);
        g = (int)(g * 0.7);
        b = (int)(b * 0.7);

        r = Math.Max(0, Math.Min(255, r));
        g = Math.Max(0, Math.Min(255, g));
        b = Math.Max(0, Math.Min(255, b));

        string darkerHex = $"#{r:X2}{g:X2}{b:X2}";
        string newContent = string.Concat(darkerHex, content.AsSpan(7));

        sb.Clear();
        sb.Append(newContent);
    }

    private void Char(int x, int y, char c, string color)
    {
        if (x >= 0 && x < width && y >= 0 && y < height)
        {
            c = c == '2' ? 'Ƨ' : c == '0' ? 'O' : c == 'R' ? 'Ꮢ' : c == 'R' ? 'Ꮢ' : c == '1' ? '˥' : c;

            var sb = frame[x, y];
            if (sb.Length >= 8)
                sb.Length = 7;
            sb.Append(color).Append(c);
        }
    }

    public void Rectangle(int x, int y, int width, int height, string color)
    {
        for (int i = 0; i < width; i++)
            for (int j = 0; j < height; j++)
                Pixel(x + i, y + j, color);
    }

    public void Circle(int centerX, int centerY, int radius, string color)
    {
        int scaledRadiusX = radius * 2;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -scaledRadiusX; dx <= scaledRadiusX; dx += 2)
                if (dx * dx + dy * dy <= radius * radius)
                {
                    Pixel(centerX + dx, centerY + dy, color);
                    Pixel(centerX + dx + 1, centerY + dy, color);
                }
    }

    public void ClearFrame()
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                frame[x, y].Clear();
    }

    private string? Cell;
    private string? PrevCell;
    private string? CBG;
    private string? CFG;
    private string? PBC;
    private string? PFC;
    private string? BGSegment;
    private string? FGSegment;
    private char? C;

    public void Render(string bacground = "#4178C8")
    {
        var sb = new StringBuilder();
        PBC = bacground;
        PFC = bacground;
        StringBuilder builder;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                builder = frame[x, y];
                Cell = builder.Length == 0 ? null : builder.ToString();
                int cellLength = Cell == null ? 0 : Cell.Length;
                int prevCellLength = PrevCell == null ? 0 : PrevCell.Length;
                if (x > 0)
                {
                    PrevCell = frame[x - 1, y].ToString().Length == 0 ? null : frame[x - 1, y].ToString();
                    prevCellLength = PrevCell == null ? 0 : PrevCell.Length;
                    PBC = PrevCell != null && prevCellLength >= 7 ? PrevCell[..7] : bacground;
                    PFC = PrevCell != null && prevCellLength >= 8 ? prevCellLength == 8 ? PrevCell[..7] : PrevCell[7..14] : bacground;
                }

                C = Cell?[^1];

                BGSegment = Cell is { Length: >= 7 } ? Cell[..7] : bacground;
                FGSegment = Cell is { Length: >= 8 } ? cellLength == 8 ? Cell[..7] : Cell[7..14] : bacground;

                CBG = (BGSegment == PBC && x > 0) ? string.Empty : SetColorBG(BGSegment);
                CFG = (FGSegment == PFC) ? string.Empty : SetColorFG(FGSegment);

                if ((x > 0 && Cell == null && PrevCell != null) || (x == 0 && Cell == null))
                    sb.Append(CBG).Append(' ');
                else if (x > 0 && Cell == PrevCell && Cell != null && (cellLength == 7 || (cellLength == 8 && Cell[^1] == ' ')) || (Cell == null))
                    sb.Append(' ');
                else if (Cell != null && cellLength > 8 && x > 0 && PrevCell == null)
                    sb.Append(CBG).Append(CFG).Append(C);
                else if (Cell != null && cellLength >= 8 && x > 0 && PrevCell != null && prevCellLength >= 8 && Cell[..(cellLength - 1)] == PrevCell[..(prevCellLength - 1)])
                    sb.Append(C);
                else if (Cell != null && cellLength > 8 && x > 0 && PrevCell != null && prevCellLength > 8 && Cell[7..cellLength] == PrevCell[7..prevCellLength])
                    sb.Append(CBG).Append(C);
                else if (Cell != null && cellLength > 8 && x > 0 && PrevCell != null && prevCellLength >= 7)
                    sb.Append(CBG).Append(CFG).Append(C);
                else if (Cell != null && cellLength == 8)
                    sb.Append(CFG).Append(C);
                else if (Cell != null && cellLength == 7 && x > 0 && PrevCell == Cell)
                    sb.Append(' ');
                else if (Cell != null && cellLength == 7 && x > 0 && PrevCell == null)
                    sb.Append(CBG).Append(' ');
                else
                    sb.Append(CBG).Append(' ');
            }
            if (y < height - 1)
                sb.AppendLine();
        }

        Console.SetCursorPosition(0, 0);
        Console.Write(SetColorBG(bacground) + sb);

        ClearFrame();
    }

    private static readonly Dictionary<string, string> _backgroundColorCache = [];
    private static readonly Dictionary<string, string> _foreGroundColorCache = [];

    public static string SetColorBG(string color)
    {
        if (_backgroundColorCache.TryGetValue(color, out var code))
            return code;

        int r = Convert.ToInt32(color.Substring(1, 2), 16);
        int g = Convert.ToInt32(color.Substring(3, 2), 16);
        int b = Convert.ToInt32(color.Substring(5, 2), 16);
        code = $"\x1b[48;2;{r};{g};{b}m";
        _backgroundColorCache[color] = code;
        return code;
    }

    public static string SetColorFG(string color)
    {
        if (_foreGroundColorCache.TryGetValue(color, out var code))
            return code;

        int idx = color.LastIndexOf('#');
        if (idx == -1 || idx + 7 > color.Length)
            code = "\x1b[38;2;255;255;255m";
        else
        {
            string hex = color.Substring(idx + 1, 6);
            int r = Convert.ToInt32(hex[..2], 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            code = $"\x1b[38;2;{r};{g};{b}m";
        }

        _foreGroundColorCache[color] = code;
        return code;
    }
}

class TrackRenderer(Renderer renderer, Track track)
{
    private readonly int width = renderer.width;
    private readonly int heightHalf = renderer.height / 2;

    private const float PitStart = 100;
    private const float PitEnd = 200;
    private float PitCurvature = 0.1f;
    private const float PitWidthMultiplier = 0.7f;

    public List<(int Left, int Right)> DrawTrack(float curvature, float distance, float trackDistance, float pitEnterProgress = 0f, bool inPit = false)
    {
        List<(int Left, int Right)> roadBoundaries = [];
        bool nearPit = distance >= PitStart && distance <= PitEnd;

        for (int y = 0; y < heightHalf; y++)
        {
            float perspective = y / (float)heightHalf;
            float invPerspective = 1.0f - perspective;

            float pow3p4 = (float)Math.Pow(invPerspective, 3.4);
            float pow2p8 = (float)Math.Pow(invPerspective, 2.8);
            float pow2 = invPerspective * invPerspective;

            float roadWidth = (0.095f + perspective * 0.8f) * 0.39f;
            float clipWidth = roadWidth * (0.12f / 0.4f);
            PitCurvature = (distance - PitStart) / 100;
            float curvePit = !inPit ? PitCurvature : 0f;
            float curveRoad = inPit ? PitCurvature * -pitEnterProgress : 0f;
            float mainMiddle = 0.5f - pitEnterProgress + (curvature + curveRoad) * pow3p4;
            float pitMiddle = 0.5f - pitEnterProgress + curvePit * pow3p4 + ((distance - PitStart) / 100);

            float pitRoadWidth = roadWidth * PitWidthMultiplier;
            float clipWidthPit = pitRoadWidth * (0.12f / 0.5f);

            int leftGrass = (int)((mainMiddle - roadWidth - clipWidth) * width);
            int leftClip = (int)((mainMiddle - roadWidth) * width);
            int rightClip = (int)((mainMiddle + roadWidth) * width);
            int rightGrass = (int)((mainMiddle + roadWidth + clipWidth) * width);
            int pitLeft = (int)((pitMiddle - pitRoadWidth) * width);
            int pitRight = (int)((pitMiddle + pitRoadWidth) * width);
            int leftClipPit = (int)((pitMiddle - pitRoadWidth) * width);
            int rightClipPit = (int)((pitMiddle + pitRoadWidth) * width);
            int rightGrassPit = (int)((pitMiddle + pitRoadWidth + clipWidthPit) * width);
            int leftGrassPit = (int)((pitMiddle - pitRoadWidth - clipWidthPit) * width);

            roadBoundaries.Add((leftClip, rightClip));

            int row = heightHalf + y;

            float finishLineDistance = (distance + 12 >= trackDistance)
                ? distance + 12 - trackDistance
                : distance + 12;

            bool isFinishLine = Math.Abs(finishLineDistance - y) <= (y / 30.0f + 0.5f);

            string grassColor = (Math.Sin(18.0f * pow2p8 + distance * 0.1f) > 0.0f)
                ? track.GrassColor1 : track.GrassColor2;

            string clipColor = (Math.Sin(50.0f * pow2 + distance) > 0.0f)
                ? track.ClipColor1 : (Math.Sin(25.0f * pow2 + distance / 2) > 0.0f)
                    ? track.ClipColor2 : track.ClipColor3;

            for (int x = 0; x < width; x++)
            {
                string color;
                if (nearPit && x > pitLeft && x < pitRight && x >= rightClip)
                    color = track.RoadColor;
                else if (nearPit &&( (x <= leftClipPit && x > leftGrassPit) || (x >= rightClipPit && x < rightGrassPit)) && (x >= rightClip && x > leftClip))
                    color = track.ClipColor1;
                else if (x < leftGrass || x >= rightGrass)
                    color = grassColor;
                else if (x < leftClip || (x >= rightClip && x < rightGrass))
                    color = clipColor;
                else if (x < rightClip)
                    color = isFinishLine
                        ? (((x / 2) + y) % 2 == 0 ? "#202020" : "#F0F0F0")
                        : track.RoadColor;
                else
                    continue;

                renderer.Pixel(x, row, color);
            }
        }

        return roadBoundaries;
    }

    public List<(int Left, int Right)> DrawPits(float curvature, float distance, float trackDistance, float pitEnterProgress = 0f, bool inPit = false)
    {
        List<(int Left, int Right)> roadBoundaries = [];

        for (int y = 0; y < heightHalf; y++)
        {
            float perspective = y / (float)heightHalf;
            float invPerspective = 1.0f - perspective;

            float pow3p4 = (float)Math.Pow(invPerspective, 3.4);
            float pow2p8 = (float)Math.Pow(invPerspective, 2.8);
            float pow2 = invPerspective * invPerspective;

            float roadWidth = (0.095f + perspective * 0.8f) * 0.39f;
            PitCurvature = (distance - PitStart) / 100;
            float curveRoad = inPit ? PitCurvature * -pitEnterProgress : 0f;
            float mainMiddle = 0.5f - pitEnterProgress + (curvature + curveRoad) * pow3p4;

            roadWidth *= PitWidthMultiplier;
            float clipWidth = roadWidth * (0.12f / 0.5f);
            int leftGrass = (int)((mainMiddle - roadWidth - clipWidth) * width);
            int leftClip = (int)((mainMiddle - roadWidth) * width);
            int rightClip = (int)((mainMiddle + roadWidth) * width);
            int rightGrass = (int)((mainMiddle + roadWidth + clipWidth) * width);

            roadBoundaries.Add((leftClip, rightClip));

            int row = heightHalf + y;

            float finishLineDistance = (distance + 12 >= trackDistance)
                ? distance + 12 - trackDistance
                : distance + 12;

            bool isFinishLine = Math.Abs(finishLineDistance - y) <= (y / 30.0f + 0.5f);

            string grassColor = (Math.Sin(18.0f * pow2p8 + distance * 0.1f) > 0.0f)
                ? track.GrassColor1 : track.GrassColor2;

            string clipColor = (Math.Sin(50.0f * pow2 + distance) > 0.0f)
                ? track.ClipColor1 : (Math.Sin(25.0f * pow2 + distance / 2) > 0.0f)
                    ? track.ClipColor2 : track.ClipColor3;

            for (int x = 0; x < width; x++)
            {
                string color;
                if (x < leftGrass || x >= rightGrass)
                    color = grassColor;
                else if (x < leftClip || (x >= rightClip && x < rightGrass))
                    color = track.ClipColor1;
                else if (x < rightClip)
                    color = isFinishLine
                        ? (((x / 2) + y) % 2 == 0 ? "#202020" : "#F0F0F0")
                        : track.RoadColor;
                else
                    continue;

                renderer.Pixel(x, row, color);
            }
        }

        return roadBoundaries;
    }
}

class SceneryRenderer(Renderer renderer, Scenery scenery)
{
    private readonly Renderer _renderer = renderer;
    private readonly int width = renderer.width;
    private readonly int height = renderer.height;
    private readonly int heightHalf = renderer.height / 2;

    public void DrawTrees(float curvature, float playerDistance)
    {
        int firstTreeIndex = (int)Math.Floor(playerDistance / scenery.TreeSpacing) + 1;
        int lastTreeIndex = (int)Math.Floor((playerDistance + scenery.MaxVisibleDistance) / scenery.TreeSpacing);

        for (int treeIndex = lastTreeIndex; treeIndex >= firstTreeIndex; treeIndex--)
        {
            float treeZ = treeIndex * scenery.TreeSpacing;
            float distanceToTree = treeZ - playerDistance;
            if (distanceToTree <= 0f || distanceToTree > scenery.MaxVisibleDistance)
                continue;

            float normalizedDistance = distanceToTree / scenery.MaxVisibleDistance;

            float verticalEasing = (1f - normalizedDistance) * (1f - normalizedDistance);
            int trunkBaseRow = heightHalf + (int)(verticalEasing * heightHalf);
            if (trunkBaseRow < 0 || trunkBaseRow >= height)
                continue;

            float dimensionScale = 0.2f + 0.8f * (1f - (float)Math.Pow(normalizedDistance, scenery.Size));

            float relativeRowPosition = (trunkBaseRow - heightHalf) / (float)heightHalf;
            float inverseRowPosition = 1f - relativeRowPosition;
            float perspectiveWeight = (float)Math.Pow(inverseRowPosition, 3.4);
            float roadWidth = (0.095f + relativeRowPosition * 0.8f) * 0.39f;
            float clippingWidth = roadWidth * (0.125f / 0.39f);
            float roadMidpoint = 0.5f + curvature * perspectiveWeight;

            int hashValue = Hash32(treeIndex ^ scenery.Seed);
            int sideMultiplier = ((hashValue & 1) == 0) ? -1 : +1;
            float jitterX = (((hashValue >> 1) & 0xFF) / 255f - 0.5f) * 0.2f;
            float jitterHeight = (((hashValue >> 9) & 0xFF) / 255f - 0.5f) * scenery.HeightJitter;

            int treeCenterX = (int)((roadMidpoint + sideMultiplier * (roadWidth + clippingWidth + scenery.RoadOffset + jitterX)) * width);

            int canopyHalfWidth = Math.Max(1, (int)(scenery.BaseCanopyHalfWidth * (1 + jitterHeight) * dimensionScale));
            int canopyHeight = Math.Max(1, (int)(scenery.BaseCanopyHeight * (1 + jitterHeight) * dimensionScale));
            int trunkHalfWidth = Math.Max(1, (int)(scenery.BaseTrunkHalfWidth * (1 + jitterHeight) * dimensionScale));
            int trunkHeight = Math.Max(1, (int)(scenery.BaseTrunkHeight * (1 + jitterHeight) * dimensionScale));

            string canopyColor = RandomizeColor(scenery.BaseCanopyColor, hashValue, scenery.ColorJitterCanopy);
            string trunkColor = RandomizeColor(scenery.BaseTrunkColor, hashValue >> 4, scenery.ColorJitterTrunk);

            for (int canopyRow = 0; canopyRow < canopyHeight; canopyRow++)
            {
                int pixelY = trunkBaseRow - trunkHeight - canopyRow;
                if (pixelY < 0 || pixelY >= height) continue;
                for (int dx = -canopyHalfWidth; dx <= canopyHalfWidth; dx++)
                {
                    int cone = dx <= 0 ? -canopyRow : canopyRow;
                    int pixelX = treeCenterX + dx - (int)(cone / scenery.ConeRatio);
                    if (pixelX >= 0 && pixelX < width)
                        _renderer.Pixel(pixelX, pixelY, canopyColor);
                }
            }

            for (int trunkRow = 0; trunkRow < trunkHeight; trunkRow++)
            {
                int pixelY = trunkBaseRow - trunkRow;
                if (pixelY < 0 || pixelY >= height) continue;
                for (int dx = -trunkHalfWidth; dx <= trunkHalfWidth; dx++)
                {
                    int pixelX = treeCenterX + dx;
                    if (pixelX >= 0 && pixelX < width)
                        _renderer.Pixel(pixelX, pixelY, trunkColor);
                }
            }
        }
    }
    
    public void DrawWalls(float curvature, float playerDistance)
    {
        int firstTreeIndex = (int)Math.Floor(playerDistance) + 1;
        int lastTreeIndex = (int)Math.Floor(playerDistance + scenery.MaxVisibleDistance);

        for (int treeIndex = lastTreeIndex; treeIndex >= firstTreeIndex; treeIndex--)
        {
            float treeZ = treeIndex;
            float distanceToTree = treeZ - playerDistance;
            if (distanceToTree <= 0f || distanceToTree > scenery.MaxVisibleDistance)
                continue;

            float normalizedDistance = distanceToTree / scenery.MaxVisibleDistance;

            float verticalEasing = (1f - normalizedDistance) * (1f - normalizedDistance);
            int trunkBaseRow = heightHalf + (int)(verticalEasing * heightHalf);
            if (trunkBaseRow < 0 || trunkBaseRow >= height)
                continue;

            float dimensionScale = 0.2f + 0.8f * (1f - (float)Math.Pow(normalizedDistance, scenery.Size));

            float relativeRowPosition = (trunkBaseRow - heightHalf) / (float)heightHalf;
            float inverseRowPosition = 1f - relativeRowPosition;
            float perspectiveWeight = (float)Math.Pow(inverseRowPosition, 3.4);
            float roadWidth = (0.095f + relativeRowPosition * 0.8f) * 0.39f;
            float clippingWidth = roadWidth * (0.125f / 0.39f);
            float roadMidpoint = 0.5f + 0 * perspectiveWeight;

            int hashValue = Hash32(treeIndex ^ scenery.Seed);
            int sideMultiplier = ((hashValue & 1) == 0) ? -1 : +1;

            int treeCenterX = (int)((roadMidpoint + sideMultiplier * (roadWidth + clippingWidth + 0)) * width);

            int trunkHalfWidth = Math.Max(1, (int)(2 * dimensionScale));
            int trunkHeight = Math.Max(1, (int)(10 * dimensionScale));

            string trunkColor = "#333333";

            for (int trunkRow = 0; trunkRow < trunkHeight; trunkRow++)
            {
                int pixelY = trunkBaseRow - trunkRow;
                if (pixelY < 0 || pixelY >= height + 20) continue;
                for (int dx = -trunkHalfWidth; dx <= trunkHalfWidth; dx++)
                {
                    int pixelX = treeCenterX + dx;
                    if (pixelX >= 0 && pixelX < width)
                        _renderer.Pixel(pixelX, pixelY, trunkColor);
                }
            }
        }
    }

    public void DrawScenery(float trackCurvature)
    {
        int sunRadius = 2;
        int sunBaseX = 10;
        int sunX = (int)(sunBaseX - trackCurvature * 40);
        int sunY = 0;
        string sunColor = "#FFB347";

        _renderer.Circle(sunX, sunY, sunRadius, sunColor);

        int hillHeight, buildingHeight;

        for (int x = 0; x < width; x++)
        {
            hillHeight = (int)Math.Abs(Math.Sin(x * 0.02 + trackCurvature + 20) * 9);
            for (int h = heightHalf - hillHeight; h < heightHalf; h++)
                _renderer.Pixel(x, h, "#2E6E41");

            buildingHeight = (int)(Math.Sin(x * 0.05 + trackCurvature * 6) * 10) >= 9 ? 13 : 0;
            for (int h = heightHalf - buildingHeight; h < heightHalf; h++)
                _renderer.Pixel(x, h, "#575F66");

            buildingHeight = (int)(Math.Sin(x * 0.03 + trackCurvature * 7) * 10) >= 9 ? 8 : 0;
            for (int h = heightHalf - buildingHeight; h < heightHalf; h++)
                _renderer.Pixel(x, h, "#705356");

            buildingHeight = (int)(Math.Sin(x * 0.04 + trackCurvature * 9) * 10) >= 9 ? 5 : 0;
            for (int h = heightHalf - buildingHeight; h < heightHalf; h++)
                _renderer.Pixel(x, h, "#463C4D");
        }
    }

    private static int Hash32(int x)
    {
        unchecked
        {
            x = ((x >> 16) ^ x) * 0x45d9f3b;
            x = ((x >> 16) ^ x) * 0x45d9f3b;
            return (x >> 16) ^ x;
        }
    }

    private static string RandomizeColor(string baseColor, int seedHash, float variationRange)
    {
        string color = baseColor.TrimStart('#');
        int r = Convert.ToInt32(color[..2], 16);
        int g = Convert.ToInt32(color.Substring(2, 2), 16);
        int b = Convert.ToInt32(color.Substring(4, 2), 16);
        float variation = ((seedHash & 0xFF) / 255f * 2f - 1f) * variationRange;
        r = Math.Clamp((int)(r * (1f + variation)), 0, 255);
        g = Math.Clamp((int)(g * (1f + variation)), 0, 255);
        b = Math.Clamp((int)(b * (1f + variation)), 0, 255);
        return $"#{r:X2}{g:X2}{b:X2}";
    }
}

class HudRenderer(Renderer renderer)
{
    private readonly Renderer _renderer = renderer;
    private readonly int width = renderer.width;
    private readonly int height = renderer.height;

    private readonly Dictionary<int, double> _lastShownGap = [];
    private readonly Dictionary<int, float> _lastGapValue = [];
    private readonly Dictionary<string, int> _previousPositions = [];
    private readonly Dictionary<string, (int Delta, double Timestamp)> _positionChangeMemory = [];

    private int _lastClassificationType = -1;
    private const double _changeDisplayDuration = 1.8;

    private double _lastIntervalUpdate = -999;
    private readonly double _minUpdateInterval = 1;
    private readonly double _minIntervalUpdateRate = 0.8;
    private readonly float _minGapDelta = 0.1f;

    public const int _cornerIndicatorWidth = 8;

    private readonly float _hudAnimSpeed = 3f;
    private float _animationProgress = 0f;
    private double _lastFrameTime = 0;

    private static readonly string[] ClassificationHeader =
    [
        "",
        "                    FREE ",
        "                    PRACTICE",
        "",
        "          LAP --/-- "
    //"Ϙ˥ ϘƧ Ϙ3 ⛈ ☀ ☁ ☂ ☃ "  ꮮꮯꮲꮶꮓꮃꮋꭺꭼꮩꭲꭰꭻꮲꮇꮪ ꬁ ꖾꕶꓳꓠꓟ ꔖ ꔪ Ⰴ⚕◀▶ ⏣  ⚠  🏴 ᎢᎠᎪᎫᎬᎳᎷᎻᏀᏃᏆᏒᏚᏙᏞᏟᏢᏦᏴ
    ];

    private static readonly List<List<(int X, int Y)>> CircuitCoordinates =
    [
        // dumb
        [(25, 7), (24, 7), (23, 7), (22, 7), (21, 7), (20, 7), (19, 7), (18, 7), (17, 7), (16, 7), (15, 7), (14, 7), (13, 7), (13, 7)],
        [(12, 7)],
        [(12, 6)],
        [(11, 6)],
        [(11, 7)],
        [(10, 7), (9, 7), (8, 7), (7, 7), (6, 7), (5, 7), (4, 7), (4, 6), (3, 6), (2, 6), (2, 6)],
        [(2, 5), (2, 4), (2, 4)],
        [(2, 4)],
        [(2, 4)],
        [(2, 3)],
        [(2, 3)],
        [(1, 3)],
        [(1, 3)],
        [(1, 2), (1, 1), (0, 1), (0, 1)],
        [(0, 0)],
        [(1, 0), (2, 0), (3, 0), (4, 0), (5, 0), (5, 0)],
        [(6, 0)],
        [(6, 1), (7, 1), (8, 1), (8, 1)],
        [(8, 2), (9, 2), (10, 2), (10, 2)],
        [(10, 3), (11, 3), (12, 3), (12, 3)],
        [(12, 3)],
        [(12, 3)],
        [(12, 4)],
        [(12, 4)],
        [(13, 4), (14, 4), (15, 4)],
        [(15, 4)],
        [(15, 4)],
        [(15, 5)],
        [(16, 5), (17, 5), (18, 5), (19, 5), (20, 5), (21, 5), (22, 5), (23, 5), (24, 5), (25, 5), (26, 5), (27, 5), (28, 5), (29, 5), (30, 5), (30, 5)],
        [(31, 5), (31, 6), (30, 6), (29, 6), (29, 7)],
        [(29, 7)],
        [(28, 7), (27, 7), (26, 7)],
        [(26, 7)]
    ];

    public void UpdateShiftLights(Car car)
    {
        int prevG, gear, carGear = car.NormalizedGear;

        (prevG, gear) = carGear switch
        {
            0 => (0, 0),
            1 => (1, 6),
            8 => (12, 14),
            _ => (carGear + 4, carGear + 5)
        };

        float speedRatio = (car.Speed * 375 - GearMaxSpeed[prevG]) / (float)(GearMaxSpeed[gear] - GearMaxSpeed[prevG]);
        int lightsOn = Math.Clamp((int)(15 * speedRatio), 0, 14);

        for (int i = 0; i < 14; i++)
        {
            string color = i switch
            {
                < 5 => "#c7fa93",
                < 9 => "#ff7a7a",
                _ => "#cc99ff"
            };
            _renderer.Text(width / 2 - 7 + i, height - 5, ".", i < lightsOn ? color : "#101317"
            );
        }
    }

    public void DrawNextCorner(int x, int y, string color, float distanceBefore, int cornerNumber, CornerType cornerType)
    {
        string cornerNumberString = (int)cornerType switch
        {
            1 or
            2 => $"Turn {cornerNumber} & {cornerNumber + 1}",
            0 => "",
            _ => $"Turn {cornerNumber}"
        };

        if (distanceBefore is > 0 and < 100)
            color = (int)(distanceBefore / 10) % 2 == 0 ? "#BDBDBD" : color;

        string[] sprite = Visuals.CornerType[(int)cornerType];

        _renderer.Text(x, y, sprite, color);
        _renderer.Text(x + 12, y + 2, cornerNumberString, "#FFFFFF");
    }

    public void DrawCircuitMap(int x, int y, string color, string indicatorColor, int distance, int section, int sectorDistance, int offset, string[] circuitMap)
    {
        int maxMapCoord = CircuitCoordinates[section].Count - 1;
        int index = Math.Clamp((int)(
                    Math.Clamp((float)(distance - (offset - sectorDistance)) / sectorDistance, 0f, 1f) * maxMapCoord),
                    0, maxMapCoord);

        (int coordX, int coordY) = CircuitCoordinates[section][index];

        string[] sprite = [.. circuitMap];

        if (coordY >= 0 && coordY < sprite.Length)
        {
            char[] row = sprite[coordY].ToCharArray();
            if (coordX >= 0 && coordX < row.Length)
                row[coordX] = ' ';
            sprite[coordY] = new string(row);
        }

        _renderer.Text(x, y, sprite, color);
        _renderer.Text(x + coordX, y + coordY, "●", indicatorColor);
    }

    public void HudBuilder(Car car, List<DRSZone> drsZones, float distance, float fullDistance)
    {
        string indicatorColor = car.DRSEngaged ? "#12c000" : drsZones.Any(zone => distance >= zone.Start && distance <= zone.End) ? "#b9e0b4" : "#000000";
        string ersColor = car.ERSState == ERSStates.Off ? "#333333" : "#F1C701";
        string ersColorDim = car.ERSState == ERSStates.Off ? "#222222" : "#8c7f3f";

        float proximityThreshold = 100.0f;

        for (int row = 0; row < 2; row++)
            for (int i = 0; i < Visuals.HudWidth; i++)
                if (Visuals.Hud[0][row][i].ToString() != " ")
                    _renderer.Text(width / 2 - 19 + i, height - 6 + row, Visuals.Hud[0][row][i], indicatorColor);

        _renderer.Text(width / 2 - 19, height - 4, Visuals.Hud[1], "#101317");
        _renderer.Text(width / 2 - 2, height - 4, Visuals.HeaderGear[car.Gear], "#FFFFFF");

        _renderer.Text(width / 2 + 5, height - 3, "км/ʜ", "#ababab");
        _renderer.Text(width / 2 + 13 - ((int)Math.Abs(car.Speed * 375)).ToString().Length, height - 3, ((int)Math.Abs(car.Speed * 375)).ToString(), "#FFFFFF");

        _renderer.Text(width / 2 - 15 - (((int)car.ERSCharge).ToString().Length - 2), height - 3, $"{(int)car.ERSCharge}%╺", ersColor);
        for (int i = 0; i < 5; i++)
        {
            string color = i * 20 < (int)car.ERSCharge ? ersColor : ersColorDim;
            _renderer.Pixel(width / 2 - 7 - i, height - 3, color);
        }
        _renderer.Text(width / 2 - 9, height - 3, "Ϟ", "#FFFFFF");
        _renderer.Text(width / 2 - 6 - car.ERSState.ToString().Length, height - 2, car.ERSState.ToString(), "#ababab");

        UpdateShiftLights(car);

        foreach (var zone in drsZones)
        {
            bool isApproachingZoneNormally = distance >= zone.Start - proximityThreshold && distance <= zone.Start;
            bool isWrappingAroundLap = distance + proximityThreshold >= fullDistance && zone.Start < proximityThreshold;
            if (isApproachingZoneNormally || isWrappingAroundLap)
            {
                float progress;

                if (isWrappingAroundLap)
                    progress = 1.0f - ((zone.Start - (distance - fullDistance)) / proximityThreshold);
                else
                    progress = 1.0f - ((zone.Start - distance) / proximityThreshold);

                progress = Math.Clamp(progress, 0.0f, 1.0f);

                int greenWidth = Math.Clamp((int)(Visuals.HudWidth * progress / 2), 0, Visuals.HudWidth / 2);

                for (int row = 0; row < 2; row++)
                    for (int i = 0; i < greenWidth; i++)
                    {
                        if (Visuals.Hud[0][row][i] != ' ')
                            _renderer.Text(width / 2 - 19 + i, height - 6 + row, Visuals.Hud[0][row][i], "#a2c79d");
                        if (Visuals.Hud[0][row][Visuals.HudWidth - 1 - i] != ' ')
                            _renderer.Text(width / 2 - 19 + Visuals.HudWidth - 1 - i, height - 6 + row, Visuals.Hud[0][row][Visuals.HudWidth - 1 - i], "#a2c79d");
                    }
                break;
            }
        }
    }

    public void TimesHud(int x, int y, string[] sectorColors, (TimeSpan time, bool buffer, byte sector) sectorTimes, TimeSpan bestTime)
    {
        int dx = width - Visuals.TimesWidth - x;
        int[] offsets = [0, 12, 24];
        for (int sector = 0; sector < 3; sector++)
            for (int i = 0; i < 12; i++)
                _renderer.Pixel(dx + offsets[sector] + i, y, sectorColors[sector]);

        _renderer.Rectangle(dx, y + 1, Visuals.TimesWidth, 3, "#222222");

        _renderer.Text(dx, y, Visuals.Times[0], "#FFFFFF");
        _renderer.Text(dx, y + 1, Visuals.Times[1], "#FFFFFF");
        _renderer.Text(dx + 17, y + 1, FormatTimeSpan(sectorTimes.time).PadLeft(18), sectorTimes.buffer ? sectorColors[sectorTimes.sector] : "#FFFFFF");
        _renderer.Text(dx, y + 2, Visuals.Times[2], "#E8002D");
        _renderer.Text(dx + 17, y + 3, bestTime.TotalMilliseconds == 0 ? "- ".PadLeft(19) : FormatTimeSpan(bestTime).PadLeft(18), "#FFFFFF");
        _renderer.Text(dx, y + 3, Visuals.Times[3], "#FFFFFF");
    }

    public void ClassificationHud(float trackLength, List<CPUCar> cpuCars, TimeSpan bestTimePlayer, float totalPlayerDistance, int y, int type, TimeSpan time, int laps, double lastLapTime, bool small)
    {
        double now = time.TotalSeconds;
        double deltaHud = now - _lastFrameTime;
        _lastFrameTime = now;

        float target = small ? 1f : 0f;
        _animationProgress = Mathy.MoveTowards(
            _animationProgress,
            target,
            _hudAnimSpeed * (float)deltaHud
        );

        var userName = "WEA";
        var entries = new List<(string Name, string Logo, string TeamColor, TimeSpan BestTime, float TotalDistance, TimeSpan Time, int Laps, double LastLapTime)>
        {(userName, "🦖", "#f17311", bestTimePlayer, totalPlayerDistance, time, laps, lastLapTime)}; //🪳

        foreach (var cpu in cpuCars)
            entries.Add((cpu.DriverName, cpu.TeamLogo, cpu.TeamColor, cpu.BestTimes[0], cpu.TotalCoveredDistance, cpu.TotalTime.Elapsed, cpu.LapsCompleted, cpu.LastLapTime));

        var sorted = type switch
        {
            0 => [.. entries
                .OrderBy(e => e.BestTime == TimeSpan.Zero)
                .ThenBy(e => e.BestTime)],
            1 or
            2 => [.. entries
                .OrderByDescending(e => e.Laps)
                .ThenByDescending(e => e.TotalDistance)],
            _ => entries
                .OrderBy(e => e.Name)
                .ToList()
        };
        bool swtchedtype = (_lastClassificationType == 0 && type != 0) || (type == 0 && _lastClassificationType != 0);
        if (swtchedtype)
        {
            _previousPositions.Clear();
            _positionChangeMemory.Clear();
        }
        _lastClassificationType = type;
        var positionDeltas = new Dictionary<string, int>();
        double currentTime = time.TotalSeconds;
        for (int i = 0; i < sorted.Count; i++)
        {
            var name = sorted[i].Name;
            int newPos = i + 1;
            if (_previousPositions.TryGetValue(name, out int oldPos))
            {
                int delta = oldPos - newPos;
                if (delta != 0)
                    _positionChangeMemory[name] = (delta, currentTime);

                positionDeltas[name] = delta;
            }
            else
                positionDeltas[name] = 0;
            _previousPositions[name] = newPos;
        }


        var fullSorted = sorted;
        int fullCount = fullSorted.Count;
        int smallCount = Math.Min(5, fullCount);

        int userIdx = fullSorted.FindIndex(e => e.Name == userName);
        int smallStart = Math.Clamp(userIdx - 2, 0, fullCount - smallCount);
        int smallEnd = smallStart + smallCount;
        int userIndex = sorted.FindIndex(e => e.Name == userName);
        int startIndex = Math.Max(0, userIndex - 2);

        int endIndex = Math.Min(sorted.Count, startIndex + 5);

        startIndex = Math.Max(0, endIndex - 5);

        float fCount = Mathy.Lerp(fullCount, smallCount, _animationProgress);
        int drawCount = Math.Clamp((int)Math.Round(fCount), 1, fullCount);

        int winStart = (int)Math.Round(Mathy.Lerp(0, smallStart, _animationProgress));
        int winEnd = winStart + drawCount;
        winEnd = Math.Min(winEnd, fullCount);
        winStart = winEnd - drawCount;
        var windowed = fullSorted.GetRange(winStart, winEnd - winStart);

        sorted = windowed;

        int posWidth = 2;
        int nameWidth = sorted.Max(e => e.Name.Length);
        int boxWidth = 30;

        int headerRows = 5;
        int totalRows = headerRows + (sorted.Count + 1) * 2 - 1;

        for (int row = 0; row < totalRows; row++)
            for (int col = 0; col < boxWidth; col++)
                _renderer.Pixel(2 + col, y + row, row < 5 ? "#1f1f1f" : "#191919");

        _renderer.Text(3, y, Visuals.F1Logo, "#444444");
        _renderer.Text(3, y, ClassificationHeader, "#FFFFFF");

        List<string> intervals = [];
        if (type != 0)
        {
            var leader = fullSorted[0];
            var nowSec = time.TotalSeconds;

            bool canRecalcAll = (nowSec - _lastIntervalUpdate) >= _minIntervalUpdateRate;
            if (canRecalcAll)
                _lastIntervalUpdate = nowSec;
            for (int i = 0; i < fullSorted.Count; i++)
            {
                var (Name, Logo, TeamColor, BestTime, TotalDistance, Time, Laps, LastLapTime) = fullSorted[i];
                float newGap;

                if (i == 0)
                {
                    intervals.Add(type == 1 ? "Interval".PadLeft(7) : "Leader".PadLeft(7));
                    if (canRecalcAll) 
                        _lastGapValue[i] = 0;
                    continue;
                }

                var lapReference = (type == 2) ? leader : fullSorted[i - 1];
                float distForLaps = lapReference.TotalDistance - TotalDistance;
                int lapDiff = (int)Math.Floor(distForLaps / trackLength);

                if (lapDiff >= 1)
                {
                    intervals.Add($"+{lapDiff} Lap{(lapDiff > 1 ? "s" : "")}".PadLeft(7));
                    continue;
                }

                if (!canRecalcAll)
                {
                    intervals.Add(("+" + _lastGapValue[i].ToString("0.000")).PadLeft(7));
                    continue;
                }

                var reference = (type == 2) ? leader : fullSorted[i - 1];
                float distDelta = reference.TotalDistance - TotalDistance;
                float lastTime = LastLapTime > 0 ? (float)LastLapTime : 67f;
                float avgSpeed = trackLength / lastTime;
                if (avgSpeed <= 0)
                {
                    intervals.Add("".PadLeft(7));
                    continue;
                }

                float gapSec = distDelta / avgSpeed;

                if (!_lastShownGap.TryGetValue(i, out var lastShown)) lastShown = -999;
                if (!_lastGapValue.TryGetValue(i, out var lastGap)) lastGap = 0;

                bool timeExpired = (nowSec - lastShown) >= _minUpdateInterval;
                bool gapJumped = Math.Abs(gapSec - lastGap) >= _minGapDelta;

                if (timeExpired || gapJumped)
                {
                    newGap = gapSec;
                    _lastShownGap[i] = nowSec;
                    _lastGapValue[i] = newGap;
                }
                else
                    newGap = lastGap;

                intervals.Add(("+" + newGap.ToString("0.000")).PadLeft(7));
            }
        }

        int startY = y + headerRows;
        for (int i = 0; i < sorted.Count; i++)
        {
            var (Name, Logo, TeamColor, BestTime, TotalDistance, Time, Laps, LastLapTime) = sorted[i];
            int fullIdx = fullSorted.FindIndex(e => e.Name == Name);
            int pos = fullSorted.FindIndex(e => e.Name == Name) + 1;
            string posStr = pos.ToString().PadLeft(posWidth);
            int lineY = startY + i * 2 + 1;

            string indicator = "";
            string indicatorColor = "#191919";

            if (_positionChangeMemory.TryGetValue(Name, out var data))
            {
                double age = currentTime - data.Timestamp;
                if (age <= _changeDisplayDuration)
                {
                    double alpha = 1.0 - (age / _changeDisplayDuration);
                    string baseColor = data.Delta > 0 ? "#00ff00" : "#ff0000";
                    indicator = data.Delta > 0 ? "▲" : "▼";
                    indicatorColor = FadeToColor(baseColor, "#191919", alpha);
                }
            }

            string nameStr = Name.PadRight(nameWidth);
            string rightSide = type == 1 || type == 2
                ? intervals[fullIdx].PadLeft(16)
                : (BestTime.TotalMilliseconds > 0
                    ? FormatTimeSpan(BestTime).PadLeft(16)
                    : "NO TIME".PadLeft(16));

            int x = 4;

            if (indicator != "")
                _renderer.Text(x - 1, lineY, indicator, indicatorColor);
            _renderer.Text(x, lineY, posStr, "#eeeeee");
            x += posWidth + 1;
            _renderer.Text(x, lineY, Logo, TeamColor);
            x += Logo.Length;
            _renderer.Text(x, lineY, "  " + nameStr, "#eeeeee");
            x += 2 + nameStr.Length;
            _renderer.Text(x, lineY, rightSide, "#eeeeee");
        }

        _renderer.Pixel(32, y + 6, "#b24ee3");
        _renderer.Pixel(33, y + 6, "#b24ee3");
        _renderer.Text(32, y + 6, "⏱", "#ffffff");
        _renderer.Text(34, y + 6, "▐", "#4178c8");
        _renderer.Pixel(35, y + 6, "#4178c7");
    }

    public void DrawCarinfo(int x, int y, string colorBody, string colorTire, string colorParts, string colorShade, double alpha)
    {
        string[] CarSprite = Visuals.CarInfo;
        int spriteHeight = CarSprite.Length;
        int spriteWidth = CarSprite[0].Length;
        colorTire = FadeToColor(colorTire, "#6b2828", Math.Clamp(1.0 - alpha / 500000, 0, 1));

        for (int dy = 0; dy < spriteHeight; dy++)
        {
            for (int dx = 0; dx < spriteWidth; dx++)
            {
                char c = CarSprite[dy][dx];
                if (c == ' ') continue;

                int px = x + dx;
                int py = y + dy;

                string sColor = c switch
                {
                    '□' => colorTire,
                    '0' => colorParts,
                    '▒' => colorShade,
                    _ => colorBody
                };

                _renderer.Pixel(px, py, sColor);
            }
        }
    }

    private static string FormatTimeSpan(TimeSpan time)
    {
        StringBuilder sb = new();

        if (time.TotalSeconds < 1)
        {
            sb.AppendFormat("{0:0.###}", time.TotalSeconds);
        }
        else if (time.TotalMinutes < 1)
        {
            sb.AppendFormat("{0:0}.{1:000}", time.Seconds, time.Milliseconds);
        }
        else
        {
            sb.AppendFormat("{0:0}:{1:00}.{2:000}", time.Minutes, time.Seconds, time.Milliseconds);
        }

        return sb.ToString();
    }

    private static string FadeToColor(string fromHex, string toHex, double alpha)
    {
        int r1 = Convert.ToInt32(fromHex.Substring(1, 2), 16);
        int g1 = Convert.ToInt32(fromHex.Substring(3, 2), 16);
        int b1 = Convert.ToInt32(fromHex.Substring(5, 2), 16);

        int r2 = Convert.ToInt32(toHex.Substring(1, 2), 16);
        int g2 = Convert.ToInt32(toHex.Substring(3, 2), 16);
        int b2 = Convert.ToInt32(toHex.Substring(5, 2), 16);

        int r = (int)(r1 * alpha + r2 * (1 - alpha));
        int g = (int)(g1 * alpha + g2 * (1 - alpha));
        int b = (int)(b1 * alpha + b2 * (1 - alpha));

        return $"#{r:X2}{g:X2}{b:X2}";
    }
}

class CarRenderer(Renderer renderer)
{
    private readonly Renderer _renderer = renderer;
    private readonly int width = renderer.width;
    private readonly int height = renderer.height;

    public void DrawPlayerCar(int startX, int startY, float distance, int direction, string colorBase, string colorShade, string colorParts, string colorAccent)
    {
        direction = (int)(distance / 10) % 2 == 0 ? direction : direction + 3;
        string[] carVisual = Visuals.Car[direction];
        int visualHeight = carVisual.Length;
        int visualWidth = carVisual[0].Length;
        for (int x = 0; x < visualWidth; x++)
        {
            int sx = startX + x;
            _renderer.Shadow(sx, startY + visualHeight - 1);
        }
        for (int y = 0; y < visualHeight; y++)
        {
            for (int x = 0; x < visualWidth; x++)
            {
                char c = carVisual[y][x];

                if (c == ' ')
                    continue;

                int px = startX + x;
                int py = startY + y;

                if (px < 0 || px > width)
                    continue;

                string sColor = c switch
                {
                    '□' => "#121212",
                    '0' => "#1f1f1f",
                    '9' => "#ff080b",
                    '▒' => colorParts,
                    '◘' => colorShade,
                    '◙' => colorAccent,
                    _ => colorBase
                };

                _renderer.Pixel(px, py, sColor);
            }
        }
    }

    public void DrawCPUCar(CPUCar car, float playerDistance, float trackLength, List<(int Left, int Right)> roadBoundaries, int idx)
    {

        float distanceToPlayer = car.Distance + 25 - playerDistance;

        if (distanceToPlayer < 0) 
            distanceToPlayer += trackLength;
        if (distanceToPlayer > trackLength)
            distanceToPlayer -= trackLength;
        else if (distanceToPlayer > 175.0f) 
            return;

        float perspective = 1.2f - (distanceToPlayer / 45.0f);
        perspective = Math.Clamp(perspective, 0.0f, 1.2f);
        int heightHalf = height / 2;
        int carY = heightHalf + (int)(heightHalf * perspective) ;
        int roadIndex = carY - heightHalf;

        if (roadIndex < 0 || roadIndex >= roadBoundaries.Count + Visuals.Car.Length)
            return;

        roadIndex = roadIndex > roadBoundaries.Count - 1 ? roadBoundaries.Count - 1 : roadIndex;
        var (left, right) = roadBoundaries[roadIndex];
        float roadCenter = (left + right) / 2.0f;

        int carX = (int)(roadCenter + car.PositionX / 4.0f * (right - left));

        int carDirection = car.TrackCurvature switch
        {
            > 0.05f => 1,
            < -0.05f => 2,
            _ => 0
        };

        carDirection = (int)(car.Distance / 10) % 2 == 0 ? carDirection : carDirection + 3;

        string[] carSprite = Visuals.Car[carDirection];

        float scale = (float)Math.Clamp(Math.Pow(perspective * 1.13, 0.5) * 1.25f, 0f, 1f);
        int scaledX;
        int prevScaledX = 0;
        for (int y = carSprite.Length - 1; y < carSprite.Length; y++)
        {
            for (int x = 0; x < carSprite[0].Length; x++)
            {
                scaledX = carX + (int)((x - carSprite[0].Length / 2) * scale);
                int scaledY = carY + (int)((y - carSprite.Length / 2) * scale);

                if (scaledX < 0 || scaledX >= width)
                    continue;
                if (scaledX == prevScaledX)
                    continue;
                if (perspective is not 0)
                    _renderer.Shadow(scaledX, scaledY - (int)((y - carSprite.Length + 1) * scale));
                prevScaledX = scaledX;
            }
        }

        for (int y = 0; y < carSprite.Length; y++)
        {
            for (int x = 0; x < carSprite[y].Length; x++)
            {
                char spriteChar = carSprite[y][x];
                if (spriteChar != ' ')
                {
                    scaledX = carX + (int)((x - carSprite[y].Length / 2) * scale);
                    int scaledY = carY + (int)((y - carSprite.Length / 2) * scale);

                    if (scaledX < 0 || scaledX >= width || scaledY < 0 || scaledY >= height + 10)
                        continue;

                    string color = spriteChar switch
                    {
                        '0' => "#1f1f1f",
                        '□' => "#121212",
                        '9' => "#ff080b",
                        '▒' => car.ColorParts,
                        '◘' => car.ColorShade,
                        '◙' => car.ColorAccent,
                        _ => car.ColorBase
                    };

                    if (perspective is 0)
                        _renderer.Text(scaledX, scaledY, '▲', "#FF1002");
                    else
                        _renderer.Pixel(scaledX, scaledY, color);
                }
            }
        }
        if (perspective is not 0)
        {
            for (int i = 0; i < 11; i++)
                _renderer.Pixel(carX - 5 + i, carY - (int)(scale * 8), "#000000");
            _renderer.Text(carX - 4, carY - (int)(scale * 8), (idx + 1).ToString(), "#ffffff");
            _renderer.Text(carX - 2, carY - (int)(scale * 8), car.TeamLogo, car.TeamColor);
            _renderer.Text(carX + 2, carY - (int)(scale * 8), car.DriverName, "#ffffff");
        }
    }
}


class CPUCar(Team team, Driver driver, float positionX, float distance) : Car
{
    private readonly float CornerLookaheadBase = 100f;
    private readonly float BaseSlowFactor = 0.4f;
    private readonly float turnRateBase = 0.1f;

    public new float Curvature { get; private set; } = positionX;
    public new float Distance { get; set; } = distance;

    public string ColorBase { get; } = team.ColorBase;
    public string ColorShade { get; } = team.ColorShade;
    public string ColorAccent { get; } = team.AccentColor ?? team.ColorBase;
    public string ColorParts { get; } = team.ColorParts;

    public string DriverName { get; } = driver.Code;
    public string TeamLogo { get; } = team.Logo;
    public string TeamColor { get; } = team.TeamColor;

    public bool[] SectorDone { get; private set; } = [false, false, false];
    public TimeSpan[] CurrentSectorTimes { get; private set; } = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero];
    public TimeSpan[] BestTimes { get; set; } = new TimeSpan[4];
    public Stopwatch Time { get; } = new Stopwatch();
    public Stopwatch TotalTime { get; } = new Stopwatch();

    public void Update(float eTime, List<CircuitSegment> circuitSegment, float trackLength, int corner)
    {
        if (!Time.IsRunning)
            Time.Start();
        if (Distance >= 0f && !TotalTime.IsRunning)
            TotalTime.Start();
        AdjustCurvature(circuitSegment, eTime);

        if (Speed >= GearMaxSpeed[Gear] / 375f)
            ShiftUp();
        else if (Gear > 0 && Speed <= GearMaxSpeed[Gear - 1] / 375f)
            ShiftDown();

        float distance = Speed * 130f * eTime;
        Distance += distance;
        TotalCoveredDistance = Distance + LapsCompleted * trackLength;

        UpdateSectorTimes(corner, trackLength);

        if (Distance < 0f)
            Distance = trackLength;
        else if (Distance >= trackLength)
        {
            SectorDone = [false, false, false];
            LapsCompleted++;
            Distance = 0;
            BestTimes[0] = Time.Elapsed < BestTimes[0] || BestTimes[0].TotalMilliseconds == 0 ? Time.Elapsed : BestTimes[0];
            LastLapTime = Time.Elapsed.TotalSeconds;
            Time.Restart();
        }

        float maxStraightSpeed = GearMaxSpeed[Gear] / 375f;

        float cornerTarget = CalculateCornerTargetSpeed(circuitSegment, Distance, maxStraightSpeed);

        float rnd = 0.96f + (float)Random.Shared.NextDouble() * 0.04f;

        float acceleration = GearAccelerations[Gear] * rnd * eTime;

        if (Speed < cornerTarget)
            Speed = MathF.Min(Speed + acceleration, cornerTarget);
        else
            Speed = MathF.Min(Speed, cornerTarget);
    }

    private float CalculateCornerTargetSpeed(List<CircuitSegment> circuitSegment, float distance, float maxStraightSpeed)
    {
        var (_, distanceToNextCorner, cornerType) = CheckNextCorner(distance, circuitSegment);

        float lookaheadModifier = GetSkillMultiplier(driver.Skill, -35f, 35f, favorLower: true);
        if (cornerType == 0 || distanceToNextCorner > CornerLookaheadBase + lookaheadModifier)
            return maxStraightSpeed;

        float baseSlow = cornerType switch
        {
            CornerType.HairpinL or CornerType.HairpinR => 0.38f,
            CornerType.ChicaneL or CornerType.ChicaneR => 0.4f,
            CornerType.NormalL or CornerType.NormalR => 0.6f,
            _ => 0.6f
        };

        float slowMult = GetSkillMultiplier(driver.Skill, 0.6f, 1.6f, favorLower: true);
        float curnvature = circuitSegment[TrackSection].Curvature >= 0.1f ? Math.Max(circuitSegment[TrackSection].Curvature, 0.8f) : circuitSegment[TrackSection].Curvature;
        baseSlow *= BaseSlowFactor * slowMult - curnvature;

        float approach = Math.Clamp(1f - distanceToNextCorner / (100 - circuitSegment[TrackSection].Length), 0f, 1f);

        float slowPct = baseSlow * approach;
        slowPct *= slowPct;

        float target = maxStraightSpeed * (1f - slowPct);

        return Math.Max(target, maxStraightSpeed * 0.1f);
    }

    private void AdjustCurvature(List<CircuitSegment> circuitSegment, float eTime)
    {
        float distanceCovered = 0.0f,
              trackCurvature = 0.0f;

        for (int i = 0; i < circuitSegment.Count; i++)
        {
            distanceCovered += circuitSegment[i].Length;
            if (Distance <= distanceCovered)
            {
                TrackSection = i;
                break;
            }
        }

        float targetCurvature = circuitSegment[TrackSection].Curvature == 0 ? Curvature : circuitSegment[TrackSection].Curvature;
        TrackCurvature += (targetCurvature - TrackCurvature) * (eTime * Speed);
        trackCurvature += Speed * eTime * eTime;

        float turnRate = turnRateBase * eTime;

        if (Curvature < targetCurvature)
        {
            Curvature += turnRate;
            if (Curvature > targetCurvature)
                Curvature = targetCurvature;
        }
        else if (Curvature > targetCurvature)
        {
            Curvature -= turnRate;
            if (Curvature < targetCurvature)
                Curvature = targetCurvature;
        }

        PositionX = (Curvature - trackCurvature) * 9;
    }

    private static float GetSkillMultiplier(byte skill, float min, float max, bool favorLower = false)
    {
        float t = skill / 255f;
        float bias = favorLower ? 1f - t : t;

        float rnd = (float)Random.Shared.NextDouble();
        float weighted = min + (max - min) * (float)Math.Pow(rnd, 1.5 - bias);

        return weighted;
    }

    public void UpdateSectorTimes(int cornerNumber, float trackDistance)
    {
        if (cornerNumber == -1 && !SectorDone[0])
        {
            if (Time.Elapsed < BestTimes[1] || BestTimes[1] == TimeSpan.Zero)
                BestTimes[1] = Time.Elapsed;

            CurrentSectorTimes[0] = Time.Elapsed;
            SectorDone[0] = true;
        }
        else if (cornerNumber == -2 && SectorDone[0] && !SectorDone[1])
        {
            TimeSpan sectorTime = Time.Elapsed - CurrentSectorTimes[0];
            if (sectorTime < BestTimes[2] || BestTimes[2] == TimeSpan.Zero)
                BestTimes[2] = sectorTime;

            CurrentSectorTimes[1] = sectorTime;
            SectorDone[1] = true;
        }
        else if (Distance >= trackDistance && SectorDone[0] && SectorDone[1] && !SectorDone[2])
        {
            TimeSpan sectorTime = Time.Elapsed - CurrentSectorTimes[0] - CurrentSectorTimes[1];
            if (sectorTime < BestTimes[3] || BestTimes[3] == TimeSpan.Zero)
                BestTimes[3] = sectorTime;
            CurrentSectorTimes[2] = sectorTime;
            SectorDone[2] = true;
        }
    }
}

class Car
{
    public int NormalizedGear => Gear switch
    {
        0 => 1,
        1 => 0,
        <= 6 => 1,
        7 => 2,
        8 => 3,
        9 => 4,
        10 => 5,
        11 => 6,
        12 => 7,
        _ => 8
    };

    private static readonly double[] GearRatios = [0, 3.86, 2.94, 2.30, 1.91, 1.59, 1.34, 1.16, 1.00, 1.00];
    private const double FinalDriveRatio = 3.9;
    private const int MaxEngineRPM = 15000;
    private const int MinEngineRPM = 5000;
    private const float TurnLowSpeedThreshold = 0.15f;
    private const float TurnHighSpeedThreshold = 0.3f;
    private const float InvSpeedDiv = 1f / 375f;

    public float TrackCurvature { get; set; }
    public float TotalCurvature { get; set; }
    public float Curvature { get; set; }

    public float TotalCoveredDistance { get; set; } = 0;
    public int LapsCompleted { get; set; } = 0;
    public double LastLapTime { get; set; }

    public int Direction { get; set; }
    public float Distance { get; set; }
    public int TrackSection { get; set; }
    public float PositionX { get; set; }


    public bool ClutchEngaged { get; set; } = true;
    public bool DRSEngaged { get; set; } = false;
    public bool Reverse { get; set; } = false;

    public int Gear { get; set; } = 1;
    public int RPM { get; private set; } = 5000;

    public float Speed { get; set; } = 0f;
    public float Acceleration { get; set; } = 0f;

    public float Drag => 0.95f + (0.2f * (DRSEngaged ? 1 : 0));
    public float DRSTurnrate => DRSEngaged ? 0.3f : 1.0f;

    public float ERSCharge { get; set; } = 100.0f;
    public ERSStates ERSState { get; set; } = ERSStates.Harvest;
    public enum ERSStates { Off, Harvest, Balanced, Attack }

    public float TireDegredation { get; set; } = 0.0f;
    public TireTypes TireType { get; set; } = TireTypes.Soft;
    public enum TireTypes { Soft, Medium, Hard, Wet, Intermediate }

    public void Accelerate(float input, float eTime)
    {
        if (!ClutchEngaged)
        {
            Speed = 0.0001f;
            return;
        }

        float baseAccel = GearAccelerations[Gear] * input;

        float boostERS = 1.0f;
        float useERS = 0.0f;

        if (ERSCharge > 0.0f)
        {
            if (ERSState == ERSStates.Balanced)
            {
                boostERS = 1.05f;
                useERS = 1.5f;
            }
            else if (ERSState == ERSStates.Attack)
            {
                boostERS = 1.2f;
                useERS = 10.0f;
            }
        }

        baseAccel *= boostERS;

        if (useERS > 0f)
        {
            ERSCharge -= useERS * eTime;
            if (ERSCharge < 0f) 
                ERSCharge = 0f;
        }

        Acceleration = baseAccel * Drag * eTime;

        Speed += Reverse ? -Acceleration : Acceleration;
    }
    public void Decelerate(float input, float eTime)
    {
        Speed += Reverse ? 0.2f * input * eTime : -(0.2f * input * eTime);
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

    public void ChargeERS(float inputRate, float eTime)
    {
        float rate = 0.0f;
        switch (ERSState)
        {
            case ERSStates.Harvest:
                rate = 10.0f;
                break;
            case ERSStates.Balanced:
                rate = 2.0f;
                break;
            case ERSStates.Attack:
            case ERSStates.Off:
                return;
        }
        if (Speed <= 0.001f)
            rate = 0.0f;
        ERSCharge += rate * inputRate * eTime;
        ERSCharge = Math.Clamp(ERSCharge, 0.0f, 100.0f);
    }
    public void UpdateRPM()
    {
        if (NormalizedGear is <= 0 or > 8)
        {
            RPM = Math.Abs(Speed) > 0 ? (int)(MinEngineRPM + Math.Abs(Speed)) : MinEngineRPM;
            return;
        }

        double speedKmS = Speed / 3.6;
        double gearRatio = FinalDriveRatio * GearRatios[NormalizedGear];
        int targetRPM = (int)(speedKmS * gearRatio * 13125);
        RPM = Math.Clamp(targetRPM, MinEngineRPM, MaxEngineRPM);
    }
    public void UpdateCurvature(float targetCurvature, float eTime)
    {
        TotalCurvature += (targetCurvature - TotalCurvature) * (eTime * Speed);
        TrackCurvature += TotalCurvature * Speed * eTime;
    }
    public void UpdateSpeed(float eTime)
    {
        Speed = Math.Clamp(Speed, Reverse ? GearMaxSpeed[0] * InvSpeedDiv : 0f, Reverse ? 0f : GearMaxSpeed[Gear] * InvSpeedDiv);
        float coveredDistance = Speed * eTime * 130;
        Distance += coveredDistance;
        TotalCoveredDistance += coveredDistance;
    }
    public bool HandleFinishLineCrossing(float trackLength, double lapTime = -1)
    {
        if (Distance < 0f)
            Distance = trackLength;
        else if (Distance >= trackLength)
        {
            if (TotalCoveredDistance >= trackLength * (LapsCompleted + 1))
            {
                if (lapTime is not -1)
                    LastLapTime = lapTime;
                LapsCompleted++;
            }
            Distance -= trackLength;
            return true; // Finished lap
        }
        return false; // Not finished lap
    }

    private float CalculateTurnRate(float eTime)
    {
        if (Speed == 0.0f)
            return 0.15f * eTime;

        if (Reverse)
            return 0.5f * eTime;

        if (Speed <= TurnLowSpeedThreshold)
        {
            float baseRate = Speed / TurnLowSpeedThreshold;
            if (baseRate < 0.15f) baseRate = 0.15f;
            return baseRate * eTime * DRSTurnrate;
        }
        else if (Speed <= TurnHighSpeedThreshold)
            return 1.0f * eTime * DRSTurnrate;

        float falloff = 1.0f - ((Speed - TurnHighSpeedThreshold) / (2.0f - TurnHighSpeedThreshold));
        return falloff * eTime * DRSTurnrate;
    }

    public void Turn(float input, float eTime)
    {
        float turnRate = CalculateTurnRate(eTime);
        Curvature -= Reverse ? -turnRate * input : turnRate * input;
        Direction = input switch
        {
            < -0.1f => 1,
            > 0.1f => 2,
            _ => 0
        };
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
    private readonly WaveOutEvent WaveOut;
    private readonly EngineWaveProvider WaveProvider;
    private Thread? SoundThread;
    private bool Running = false;
    private double RPM = 5000;
    private int Gear = 1;
    private double Acceleration = 0;
    private float Proximity = 0;
    private float Pan = 0.5f;
    public bool Pause { get; set; }

    public EngineSound()
    {
        WaveProvider = new EngineWaveProvider(
            () => RPM,
            () => Gear,
            () => Acceleration,
            () => Proximity,
            () => Pan
        );
        WaveOut = new WaveOutEvent();
        WaveOut.Init(WaveProvider);
    }

    public void UpdateEngineState(double rpm, int gear, double acceleration, float proximity = 1f, float pan = 0.5f)
    {
        RPM = rpm;
        Gear = gear;
        Acceleration = acceleration;
        Proximity = proximity;
        Pan = Math.Clamp(pan, 0f, 1f);
    }

    public void StartSound()
    {
        Running = true;
        Pause = false;
        WaveOut.Play();
        SoundThread = new Thread(SoundUpdateLoop);
        SoundThread.Start();
    }

    private void SoundUpdateLoop()
    {
        while (Running)
        {
            Thread.Sleep(100);
        }
    }

    public void StopSound()
    {
        Running = false;
        SoundThread?.Join();
        WaveOut.Stop();
        WaveOut.Dispose();
    }

    public void PauseSound()
    {
        Pause = true;
        WaveOut.Pause();
    }

    public void UnpauseSound()
    {
        Pause = false;
        WaveOut.Play();
    }
}
class EngineWaveProvider : WaveProvider32
{
    // Engine parameters
    private readonly Func<double> getRPM;
    private readonly Func<int> getGear;
    private readonly Func<double> getAcceleration;
    private readonly Func<float> getProximity;
    private readonly Func<float> getPan;

    // Audio parameters
    private readonly int sampleRate = 44100;
    private readonly Random random = new();

    // Phase states for different components
    private double phaseMain = 0;
    private double phaseTurbo = 0;
    private double phaseMGUH = 0;

    // Sound effect states
    private double lastSample = 0;

    public EngineWaveProvider(Func<double> rpmFunc, Func<int> gearFunc, Func<double> accelerationFunc, Func<float> proximity, Func<float> panFunc)
    {
        SetWaveFormat(sampleRate, 2);
        getRPM = rpmFunc;
        getGear = gearFunc;
        getAcceleration = accelerationFunc;
        getProximity = proximity;
        getPan = panFunc;
    }

    public override int Read(float[] buffer, int offset, int sampleCount)
    {
        double rpm = getRPM();
        double acceleration = Math.Max(0, getAcceleration()); // 0-1 range
        int gear = getGear();
        float proximity = getProximity();

        // 1. Base parameters
        if (proximity != 1f)
            proximity /= 3;
        double baseFreq = rpm / 180 * 3; // V6 firing frequency (3 pulses/rev)
        double volume = 0.3 + (0.4 * rpm / 15000); // Volume scales with RPM
        double gearFactor = 0.3 + (gear / 8.0); // Gear-based timbre adjustment

        if (proximity <= 0f)
        {
            Array.Clear(buffer, offset, sampleCount); // Write silence to both channels
            return sampleCount;
        }

        for (int i = 0; i < sampleCount / 2; i++)
        {
            // 2. Main engine harmonics (V6 characteristic)
            double engine = 0;
            for (int h = 1; h <= 12; h++)
            {
                double weight = 1.0 / Math.Pow(h, 0.85); // Natural harmonic decay

                // Emphasize V6 characteristic harmonics
                if (h % 3 == 0) weight *= 2.0; // 3rd, 6th, 9th, 12th

                // RPM-based timbre changes
                if (rpm > 10000) weight *= 1.0 + (h * 0.02);

                engine += weight * Math.Sin(phaseMain * h);
            }
            engine *= volume * gearFactor * 0.25;

            // 3. Turbocharger whistle (key F1 sound)
            double turboFreq = 3500 + (rpm * 0.18);
            double turbo = 0.7 * Math.Sin(phaseTurbo)
                        * Math.Sin(phaseTurbo * 3.0)
                        * acceleration * gearFactor;

            // 4. Hybrid system whine (MGU-H)
            double mguhFreq = 666 + (rpm * 0.1);
            double hybrid = 0.1 * Math.Sin(phaseMGUH * 0.5)
                         * Math.Sin(phaseMGUH * 4.0)
                         * gearFactor / 2;

            // 5. Combustion roughness
            double roughness = (random.NextDouble() - 0.5)
                             * 0.08 * acceleration
                             * (rpm / 15000);

            engine *= proximity;
            turbo *= proximity;
            hybrid *= proximity;
            roughness *= proximity;
            acceleration *= proximity;

            // 6. Combine components
            double rawSample = engine
                + turbo * (0.3 + acceleration * 0.5)
                + hybrid * 0.4
                + roughness;


            // 7. Dynamic processing
            // Waveshaping for distortion
            double shapedSample = 1.8 * Math.Tanh(rawSample * 0.7);

            // RPM-adaptive low-pass filter
            double filterCutoff = 0.2 + (0.75 * rpm / 15000);
            double filteredSample = shapedSample * (1 - filterCutoff)
                                 + lastSample * filterCutoff;
            lastSample = filteredSample;

            float pan = Math.Clamp(getPan(), 0f, 1f);
            float left = (float)(filteredSample * (1f - pan));
            float right = (float)(filteredSample * pan);
            buffer[offset + i * 2] = left;
            buffer[offset + i * 2 + 1] = right;

            // Update phases
            double phaseIncrement = 2 * Math.PI * baseFreq / sampleRate;
            phaseMain += phaseIncrement;
            phaseTurbo += 2 * Math.PI * turboFreq / sampleRate;
            phaseMGUH += 2 * Math.PI * mguhFreq / sampleRate;

            // Phase wrapping
            if (phaseMain > 2 * Math.PI) phaseMain -= 2 * Math.PI;
            if (phaseTurbo > 2 * Math.PI) phaseTurbo -= 2 * Math.PI;
            if (phaseMGUH > 2 * Math.PI) phaseMGUH -= 2 * Math.PI;
        }
        return sampleCount;
    }
}


static class Mathy
{
    public static float Lerp(float a, float b, float t)
    {
        return a + (b - a) * t;
    }

    public static float MoveTowards(float current, float target, float maxDelta)
    {
        if (Math.Abs(target - current) <= maxDelta)
            return target;
        return current + Math.Sign(target - current) * maxDelta;
    }

    public static float Wrap(float value, float max)
    {
        float m = value % max;
        return m < 0 ? m + max : m;
    }
}
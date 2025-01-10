using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static partial class Keyboard
{
    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
    public static bool IsKeyPressed(ConsoleKey key)
    {
        return (GetAsyncKeyState((int)key) & 0x8000) != 0;
    }
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

        Console.ReadKey();

        Renderer renderer = new(Console.LargestWindowWidth, Console.LargestWindowHeight);
        TrackRenderer trackRenderer = new(renderer);
        SceneryRenderer sceneryRenderer = new(renderer);
        HudRenderer hudRenderer = new(renderer);

        var vecTrack = CircuitVecs;

        int direction, nTrackSection, nCarX, nCarY = Console.LargestWindowHeight - 15;

        long elapsedTime;


        float fCurvature = 0.0f, fTrackCurvature = 0.0f, fPlayerCurvature = 0.0f, fTrackDistance = 0.0f,
              fDistance = 0.0f, fCarPos = 0.0f, turnRate, fOffset, fTargetCurvature, fTrackCurveDiff,
              lowSpeedThreshold = 0.1f, highSpeedThreshold = 0.4f;

        const int targetFps = 39,
                  frameTime = 1000 / targetFps;

        const float fElapsedTime = (float)frameTime / 1000;

        foreach (var t in vecTrack)
            fTrackDistance += t.Distance;

        Stopwatch stopwatch = new();
        stopwatch.Start();

        Stopwatch keyDelay = new();

        keyDelay.Start();

        bool[] sector = [true, true, true];

        Stopwatch[] sectorTimers = [ new(), new(), new() ];

        TimeSpan[] currentSectorTimes = [new(0), new(0), new(0)];

        Stopwatch time = new();
        time.Start();

        while (true)
        {
            stopwatch.Restart();

            direction = 0;

            turnRate = CalculateTrunRate(lowSpeedThreshold, highSpeedThreshold, fElapsedTime);

            #region controls
            if (Keyboard.IsKeyPressed(ConsoleKey.W))
                Car.Accelerate(1.0f, fElapsedTime);
            else
                Car.Decelerate(0.6f, fElapsedTime);
            if (Keyboard.IsKeyPressed(ConsoleKey.A))
            {
                direction = 2;
                fPlayerCurvature -= Car.Reverse ? -turnRate : turnRate;
                if (Car.Gear != 1)
                    Car.Decelerate(0.01f, fElapsedTime);
            }
            if (Keyboard.IsKeyPressed(ConsoleKey.D))
            {
                direction = 1;
                fPlayerCurvature += Car.Reverse ? -turnRate : turnRate;
                if (Car.Gear != 1)
                    Car.Decelerate(0.01f, fElapsedTime);
            }
            if (Keyboard.IsKeyPressed(ConsoleKey.S))
                Car.Decelerate(3.0f, fElapsedTime);
            if (Keyboard.IsKeyPressed(ConsoleKey.Spacebar))
                Car.ClutchEngaged = true;
            if (Keyboard.IsKeyPressed(ConsoleKey.R) && keyDelay.ElapsedMilliseconds > 350)
            {
                Car.ReverseGear();
                keyDelay.Restart();
            }
            // Automatic Gearbox
            if (Car.Speed >= Car.GearMaxSpeed[Car.Gear] / 375.0f)
                Car.ShiftUp();
            else if (Car.Gear > 0 && Car.Speed <= Car.GearMaxSpeed[Car.Gear - 1] / 375.0f)
                Car.ShiftDown();
            #endregion

            if ((fCarPos * fCurvature > 0 && Math.Abs(fCarPos) > 0.6f) || Math.Abs(fCarPos - fCurvature) > 0.6f && !Car.Reverse)
            {
                Car.Speed += (Car.Reverse ? 1 : -1) * Math.Abs(fCurvature * 5 * Car.Speed * (Car.Gear / 5) + 1.0f) * fElapsedTime;
            }

            Car.Speed = Math.Clamp(Car.Speed, Car.Reverse ? Car.GearMaxSpeed[0] / 375.0f : 0.0f, Car.Reverse ? 0.0f : Car.GearMaxSpeed[Car.Gear] / 375.0f);

            fOffset = 0;
            nTrackSection = 0;

            // Find position on track
            while (nTrackSection < vecTrack.Count - 1 && fOffset <= fDistance)
            {
                fOffset += vecTrack[nTrackSection].Distance;
                nTrackSection++;
            }

            fDistance += Car.Speed * 130 * fElapsedTime;

            //Green #44c540  Yellow #e8c204  Purple #aa29cc
            if (vecTrack[nTrackSection].CornerNumber == -1 && sector[0])
            {
                if (time.Elapsed < BestTime[1] || BestTime[1].TotalSeconds < 1)
                {
                    BestTime[1] = time.Elapsed;
                    HudRenderer.SectorColors[0] = "#44c540";
                }
                else
                {
                    HudRenderer.SectorColors[0] = "#e8c204";
                }
                currentSectorTimes[0] = time.Elapsed;
                sectorTimers[0].Start();
                sector[0] = false;
            }
            else if (vecTrack[nTrackSection].CornerNumber == -2 && sector[1])
            {
                if (time.Elapsed - BestTime[1] < BestTime[2] || BestTime[2].TotalSeconds < 1)
                {
                    BestTime[2] = time.Elapsed - BestTime[1];
                    HudRenderer.SectorColors[1] = "#44c540";
                }
                else
                {
                    HudRenderer.SectorColors[1] = "#e8c204";
                }
                currentSectorTimes[1] = time.Elapsed;
                sectorTimers[1].Start();
                sector[1] = false;
            }
            else if (fDistance >= fTrackDistance && sector[2])
            {
                if (time.Elapsed - BestTime[1] - BestTime[2] < BestTime[3] || BestTime[3].TotalSeconds < 1)
                {
                    BestTime[3] = time.Elapsed - BestTime[1] - BestTime[2];
                    HudRenderer.SectorColors[2] = "#44c540";
                }
                else
                {
                    HudRenderer.SectorColors[2] = "#e8c204";
                }
                currentSectorTimes[2] = time.Elapsed;
                sectorTimers[2].Start();
                sector[2] = false;
            }

            if (fDistance >= fTrackDistance)
            {
                fDistance -= fTrackDistance;
                sector[0] = true;
                sector[1] = true;
                sector[2] = true;
                if (time.Elapsed < BestTime[0] || BestTime[0].TotalSeconds == 0)
                {
                    BestTime[0] = time.Elapsed;
                }
                time.Restart();
            }

            if (sectorTimers[2].ElapsedMilliseconds >= 3000)
            {
                HudRenderer.SectorColors[0] = "#333333";
                HudRenderer.SectorColors[1] = "#333333";
                HudRenderer.SectorColors[2] = "#333333";
                sectorTimers[0].Reset();
                sectorTimers[1].Reset();
                sectorTimers[2].Reset();
            }

            else if (fDistance < 0)
            {
                fDistance = 0; Car.Speed = 0;
            }

            if (nTrackSection > 0)
            {
                fTargetCurvature = vecTrack[nTrackSection - 1].Curvature;
                fTrackCurveDiff = (fTargetCurvature - fCurvature) * (fElapsedTime * Car.Speed);

                fCurvature += fTrackCurveDiff;

                fTrackCurvature += fCurvature * Car.Speed * fElapsedTime;

                trackRenderer.DrawTrack(fCurvature, fDistance, fTrackDistance);
                sceneryRenderer.DrawScenery(fTrackCurvature);
            }

            if (sectorTimers[0].ElapsedMilliseconds <= 3000 && sectorTimers[0].ElapsedMilliseconds > 0)
                hudRenderer.TimesHud(currentSectorTimes[0]);
            else if (sectorTimers[1].ElapsedMilliseconds <= 3000 && sectorTimers[1].ElapsedMilliseconds > 0)
                hudRenderer.TimesHud(currentSectorTimes[1]);
            else if (sectorTimers[2].ElapsedMilliseconds <= 3000 && sectorTimers[2].ElapsedMilliseconds > 0)
                hudRenderer.TimesHud(currentSectorTimes[2]);
            else
                hudRenderer.TimesHud(time.Elapsed);

            fCarPos = fPlayerCurvature - fTrackCurvature * 7.5f;
            nCarX = Console.LargestWindowWidth / 2 + ((int)(Console.LargestWindowWidth * fCarPos) / 2) - 20;

            renderer.DrawCar(nCarX, nCarY, "#E8002D", direction);

            hudRenderer.DrawCircuitMap(Console.LargestWindowWidth / 2 - HudRenderer.CircuitSprite[0].Length / 2, 1, "#FFFFFF");

            hudRenderer.HudBuilder();
            hudRenderer.SectorsHud();

            renderer.DisplayFrame();

            elapsedTime = stopwatch.ElapsedMilliseconds;
            if (elapsedTime < frameTime)
                Thread.Sleep((int)(frameTime - elapsedTime));
        }
    }

    public static TimeSpan[] BestTime = new TimeSpan[4];

    public static readonly List<CircuitSegment> CircuitVecs =
    [
            new CircuitSegment(0.0f, 500.0f, 0, false),
        new CircuitSegment(0.9f, 70.0f, 1, true),
            new CircuitSegment(-0.9f, 35.0f, 1, false),
        new CircuitSegment(-1.2f, 80.0f, 2, true),
            new CircuitSegment(1.0f, 50.0f, 2, false),
        new CircuitSegment(0.2f, 400.0f, 3, true),
            new CircuitSegment(0.0f, 300.0f, 3, false),
            new CircuitSegment(0.0f, 10.0f, 0, false),
            new CircuitSegment(0.0f, 10.0f, -1, false),
        new CircuitSegment(-1.0f, 50.0f, 4, true),
            new CircuitSegment(1.0f, 50.0f, 4, false),
        new CircuitSegment(1.0f, 50.0f, 5, true),
            new CircuitSegment(-1.0f, 25.0f, 5, false),
            new CircuitSegment(0.0f, 200.0f, 5, false),
        new CircuitSegment(1.0f, 50.0f, 6, true),
            new CircuitSegment(0.0f, 200.0f, 6, false),
        new CircuitSegment(0.8f, 50.0f, 7, true),
            new CircuitSegment(0.0f, 200.0f, 7, false),
            new CircuitSegment(-0.1f,200.0f, 7, false),
            new CircuitSegment(0.0f, 400.0f, 7, false),
            new CircuitSegment(0.0f, 10.0f, 0, false),
            new CircuitSegment(0.0f, 10.0f, -2, false),
        new CircuitSegment(-1.0f, 70.0f, 8, true),
            new CircuitSegment(1.0f, 70.0f, 8, false),
        new CircuitSegment(0.4f, 150.0f, 9, true),
            new CircuitSegment(-0.3f, 75.0f, 9, false),
        new CircuitSegment(-1.0f, 80.0f, 10, true),
            new CircuitSegment(1.0f, 50.0f, 10, false),
            new CircuitSegment(0.0f, 500.0f, 10, false),
        new CircuitSegment(0.25f, 300.0f, 11, true),
            new CircuitSegment(-0.1f, 50.0f, 11, false),
            new CircuitSegment(0.0f, 150.0f, 11, false)
    ];
    private static float CalculateTrunRate(float lowSpeedThreshold, float highSpeedThreshold, float fElapsedTime)
    {
        if (Car.Speed == 0.0f)
            return 0.15f * fElapsedTime;
        else if (Car.Reverse)
            return (Car.Speed / lowSpeedThreshold < 0.25f ? 0.25f : Car.Speed / lowSpeedThreshold) * fElapsedTime;
        else if (Car.Speed <= lowSpeedThreshold)
            return (Car.Speed / lowSpeedThreshold < 0.15f ? 0.15f : Car.Speed / lowSpeedThreshold) * fElapsedTime;
        else if (Car.Speed <= highSpeedThreshold)
            return 1.0f * fElapsedTime;
        else
            return (1.0f - ((Car.Speed - highSpeedThreshold) / (2.0f - highSpeedThreshold))) * fElapsedTime;
    }
}
public class CircuitSegment(float curvature, float distance, int cornerNumber, bool isReal)
{
    public float Curvature { get; set; } = curvature;
    public float Distance { get; set; } = distance;
    public int CornerNumber { get; set; } = cornerNumber;
    public bool IsReal { get; set; } = isReal;
}
class Renderer(int width, int height)
{
    private readonly int width = width;

    private readonly int height = height;

    private string[,] frame = new string[width, height];

    public static readonly string[][] CarSprite =
[
    [
            "     □□□□□       █████       □□□□□     ",
            "     □□□□□███  █████████  ███□□□□□     ",
            "     □□□□█████████████████████□□□□     ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "□□□□□□□ ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ □□□□□□□",
            "□□□□□□□◘◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘◘□□□□□□□",
            "□□□□□□□◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘□□□□□□□",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "      □□□□□      █████        □□□□□    ",
            "     □□□□□███  █████████  ███□□□□□     ",
            "    □□□□□█████████████████████□□□      ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "□□□□□□□ ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ □□□□□□□",
            "□□□□□□□◘◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘◘□□□□□□□",
            "□□□□□□□◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘□□□□□□□",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
        [
            "    □□□□□        █████      □□□□□      ",
            "     □□□□□███  █████████  ███□□□□□     ",
            "      □□□█████████████████████□□□□□    ",
            "      ▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒      ",
            "□□□□□□□ ◘◘◘◘▒◘◘◘◘◘◘◘◘◘◘◘◘◘▒◘◘◘◘ □□□□□□□",
            "□□□□□□□◘◘◘◘◘◘◘◘◘◘◘▒▒▒◘◘◘◘◘◘◘◘◘◘◘□□□□□□□",
            "□□□□□□□◘◘◘▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒▒◘◘◘□□□□□□□",
            "□□□□□□□ ▒ ▒ ▒ ▒ ▒     ▒ ▒ ▒ ▒ ▒ □□□□□□□"
        ],
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
    public void DrawCar(int startX, int startY, string hex, int direction)
    {
        int spriteHeight = CarSprite[direction].Length;
        int spriteWidth = CarSprite[direction][0].Length;
        int frameX;

        for (int y = 0; y < spriteHeight; y++)
        {
            frameX = 0;
            for (int x = 0; x < spriteWidth; x++)
            {
                if (startX + x >= 0 && startX + x < width && startY + y >= 0 && startY + y < height &&CarSprite[direction][y][x] != ' ')
                {
                    if (CarSprite[direction][y][x] == '□' && x < spriteWidth)
                        frame[startX + frameX, startY + y] = "#000000";
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
                    _renderer.Pixel(x, nRow, ((x / 2) + y) % 2 == 0 ? "#F0F0F0" : "#202020");
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
    private readonly Renderer _renderer = renderer;
    private readonly int screenHeightHalf = Console.LargestWindowHeight / 2;
    private readonly int screenWidth = Console.LargestWindowWidth;
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
    public static string[] hud =
    [
        "          ╭────────────────╮          ",
        "──────────╯ ․․․․․․․․․․․․․․ ╰──────────",
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
    public static readonly string[] CircuitSprite =
    [
        "╭─────╮                         ",
        "╰╮    ╰─╮                       ",
        " │      ╰─╮                     ",
        " │        ╰─╮                   ",
        " ╰╮         ╰──╮                ",
        "  │            ╰───────────────╮",
        "  ╰─╮                        ╭─╯",
        "    ╰───────────────●────────╯  "
    ];

    public void DrawCircuitMap(int x, int y, string hex)
    {
        _renderer.Text(x, y, CircuitSprite, hex);
    }
    public void HudBuilder()
    {
        _renderer.Text(Console.LargestWindowWidth / 2 - 19,  Console.LargestWindowHeight - 6, hud, "#0A1928");
        _renderer.Text(Console.LargestWindowWidth / 2 - 2, Console.LargestWindowHeight - 4, HeaderGear[Car.Gear], "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth / 2 + 5, Console.LargestWindowHeight - 3, "км/ʜ", "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth / 2 + 13 - ((int)Math.Abs(Car.Speed * 375)).ToString().Length, Console.LargestWindowHeight - 3, ((int)Math.Abs(Car.Speed * 375)).ToString(), "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth / 2 - 15,  Console.LargestWindowHeight - 3, "23%╺", "#F1C701");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 11, Console.LargestWindowHeight - 3, "#F1C701");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 10, Console.LargestWindowHeight - 3, "#F1C701");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 9,  Console.LargestWindowHeight - 3, "#8c7f3f");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 8,  Console.LargestWindowHeight - 3, "#8c7f3f");
        _renderer.Pixel(Console.LargestWindowWidth / 2 - 7,  Console.LargestWindowHeight - 3, "#8c7f3f");
        _renderer.Text(Console.LargestWindowWidth / 2 - 9, Console.LargestWindowHeight - 3, "Ϟ", "#0A1928");
    }
    public void TimesHud(TimeSpan time)
    {
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 2, times[1], "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length + 16, 2, FormatTimeSpan(time), "#FFFFFF");
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 2, "#222222");

        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 3, "#222222");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 3, times[2], "#E8002D");

        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 4, "#222222");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length + 16, 4, FormatTimeSpan(Racing.BestTime[0]).ToString(), "#FFFFFF");
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 4, times[3], "#FFFFFF");
    }
    public void SectorsHud()
    {
        _renderer.Text(Console.LargestWindowWidth - times[0].Length - 1, 1, times[0], "#FFFFFF");
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length - 1, 1, SectorColors[0]);
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length + 11, 1, SectorColors[1]);
        _renderer.Pixel(Console.LargestWindowWidth - times[0].Length + 23, 1, SectorColors[2]);
    }
    public static string[] SectorColors =
    [
        "#333333",
        "#333333",
        "#333333"
    ];
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
public static class Car
{
    public static readonly float[] GearAccelerations =
    [
        0.1f,                           // R
        0.00001f,                       // N
        0.01f, 0.02f, 0.04f, 0.12f,     // Gear 1 Standing Start Sim
        0.29f,  // Gear 1                         
        0.22f,  // Gear 2
        0.19f,  // Gear 3
        0.17f,  // Gear 4
        0.13f,  // Gear 5
        0.10f,  // Gear 6
        0.08f,  // Gear 7
        0.05f, 0.02f, 0.007f    // Gear 8 Gradual        
    ];

    public static readonly float[] GearMaxSpeed =
    [
        -20.0f,                         // R
        0.00001f,                       // N
        0.01f, 2.0f, 11.0f, 19.0f,      // Gear 1 Standing Start Sim    
        80.0f,  // Gear 1        
        120.0f, // Gear 2            
        155.0f, // Gear 3        
        190.0f, // Gear 4        
        220.0f, // Gear 5        
        250.0f, // Gear 6        
        280.0f, // Gear 7        
        300.0f, 316.0f, 375.0f  // Gear 8 Gradual        
    ];

    public static float DRS => DRSEngaged ? 0.7f : 1.0f;
    public static float Drag = 0.02f * Speed * DRS;
    public static float Speed = 0.0f;
    public static float Acceleration = 0.00f;
    public static int Gear = 1;
    public static bool ClutchEngaged = true;
    public static bool DRSEngaged = false;
    public static bool Reverse = false;

    public static void Accelerate(float input, float fElapsedTime)
    {
        if (ClutchEngaged)
        {
            Speed += Reverse ? -((GearAccelerations[Gear] * input - Drag) * fElapsedTime) 
                             :   (GearAccelerations[Gear] * input - Drag) * fElapsedTime;
            Acceleration = (GearAccelerations[Gear] * input) - Drag;
        }
        else
        {
            Speed = 0.0001f;
        }
    }
    public static void Decelerate(float input, float fElapsedTime)
    {
        Speed += Reverse ? 0.2f * input * fElapsedTime : -(0.2f * input * fElapsedTime);
        Acceleration = 0;
    }
    public static void ShiftUp() => Gear = (Gear < GearAccelerations.Length - 1) && Gear > 0 ? Gear + 1 : Gear;
    public static void ShiftDown() => Gear = Gear > 1 ? Gear - 1 : Gear;
    public static void ReverseGear()
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
}

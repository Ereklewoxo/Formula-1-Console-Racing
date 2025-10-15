namespace RacingConsole.Models;

public class Team
{
    public string Id { get; set; }
    public string ColorBase { get; set; }
    public string ColorShade { get; set; }
    public string ColorParts { get; set; }
    public string Logo { get; set; }
    public string TeamColor { get; set; }
    public string? AccentColor { get; set; }
    public List<Driver> Drivers { get; set; } = [];
}

public class Driver
{
    public string Code { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string Nationality { get; set; }
    public byte Skill { get; set; }
}


public class GrandPrix
{
    public string Id { get; set; }
    public Circuit Circuit { get; set; }
    public Track Track { get; set; }
    public Scenery Scenery { get; set; }
}

public class Circuit
{
    public string Id { get; set; }
    public string DisplayName { get; set; }
    public List<CircuitSegment> Segments { get; set; }
    public List<DRSZone> DRSZones { get; set; }
    public string[] CircuitMap { get; set; }
}

public class CircuitSegment
{
    public float Curvature { get; set; }
    public float Length { get; set; }
    public int Section { get; set; }
    public CornerType CornerType { get; set; }
}

public class DRSZone
{
    public float Start { get; set; }
    public float End { get; set; }
}

public class Track
{
    public string GrassColor1 { get; set; }
    public string GrassColor2 { get; set; }
    public string ClipColor1 { get; set; }
    public string ClipColor2 { get; set; }
    public string ClipColor3 { get; set; }
    public string RoadColor { get; set; }
}

public class Scenery
{
    public int Seed { get; set; }
    public float MaxVisibleDistance { get; set; }
    public float TreeSpacing { get; set; }
    public float Size { get; set; }
    public float HeightJitter { get; set; }
    public float RoadOffset { get; set; }
    public float ConeRatio { get; set; }
    public int BaseCanopyHalfWidth { get; set; }
    public int BaseCanopyHeight { get; set; }
    public int BaseTrunkHalfWidth { get; set; }
    public int BaseTrunkHeight { get; set; }
    public string BaseCanopyColor { get; set; }
    public float ColorJitterCanopy { get; set; }
    public string BaseTrunkColor { get; set; }
    public float ColorJitterTrunk { get; set; }
    public string SkyColor { get; set; }
}

public enum CornerType
{
    None,
    ChicaneR,
    ChicaneL,
    HairpinR,
    HairpinL,
    NormalR,
    NormalL
}
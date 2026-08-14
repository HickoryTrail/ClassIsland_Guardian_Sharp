namespace ClassIslandGuardian.Guardian;

public sealed record GuardianConfiguration(
    string ClassIslandPath,
    string ClassIslandProcessName,
    string ClassIslandLauncherName,
    string PasswordHash)
{
    public const string DefaultProcessName = "ClassIsland.Desktop.exe";
    public const string DefaultLauncherName = "ClassIsland.exe";
}

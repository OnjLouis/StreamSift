namespace StreamSift;

internal static class Ascii
{
    internal static bool IsDigit(char value) => value is >= '0' and <= '9';
    internal static bool IsLower(char value) => value is >= 'a' and <= 'z';
    internal static bool IsLetterOrDigit(char value) => IsLower(value) || value is >= 'A' and <= 'Z' || IsDigit(value);
}

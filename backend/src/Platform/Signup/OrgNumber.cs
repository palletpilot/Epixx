namespace Lagerkraft.Platform.Signup;

public static class OrgNumber
{
    public static string Normalize(string raw)
    {
        var digits = new string((raw ?? "").Where(char.IsDigit).ToArray());
        return digits;
    }

    public static bool IsValid(string? raw)
    {
        var digits = Normalize(raw ?? "");
        if (digits.Length != 10)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < 9; i++)
        {
            var n = (digits[i] - '0') * (i % 2 == 0 ? 2 : 1);
            sum += n / 10 + n % 10;
        }

        var check = (10 - sum % 10) % 10;
        return check == digits[9] - '0';
    }

    public static string Format(string digits)
    {
        digits = Normalize(digits);
        return digits.Length == 10 ? $"{digits[..6]}-{digits[6..]}" : digits;
    }
}

namespace Mailserver.Web;

public static class PasswordRules
{
    public const int MinLength = 10;

    /// <summary>Returns a German error text, or null if the password is acceptable.</summary>
    public static string? Check(string? password, string? confirm)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
        {
            return $"Das Passwort muss mindestens {MinLength} Zeichen lang sein.";
        }

        return password != confirm ? "Die beiden Passwörter stimmen nicht überein." : null;
    }
}

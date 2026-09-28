using System.Security.Cryptography;
using System.Text;
using LabbyTwo.Storage;

namespace LabbyTwo.Services;

/// <summary>
/// Whether a username and password are the configured ones, in time that does not depend
/// on how much of either was right. Both sides are hashed first, so the comparison is
/// always of 32 bytes and says nothing about the password's length either. The username
/// is compared the same way, and both are always compared: stopping at a wrong username
/// would make a right one measurably slower to refuse.
/// </summary>
public static class LoginCredentials
{
    public static bool Matches(LabbyOptions.AuthSettings auth, string? username, string? password)
    {
        // With no password configured there is no login to pass, and an empty form must
        // not count as knowing the empty password.
        if (!auth.Enabled)
            return false;

        var user = FixedTimeEquals((username ?? "").ToUpperInvariant(), auth.Username.ToUpperInvariant());
        var pass = FixedTimeEquals(password ?? "", auth.Password);
        return user & pass;
    }

    public static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

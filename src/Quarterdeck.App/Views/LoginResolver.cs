using System.Windows;
using Quarterdeck.Core.Model;
using Quarterdeck.Core.Security;
using Quarterdeck.Core.Security.KeePassXc;

namespace Quarterdeck.App.Views;

/// <summary>
/// User name and password for tabs that sign in themselves (RDP, SQL): from the shortcut,
/// from KeePassXC, or asked. Not saving the password is the recommended setup, so asking is
/// the normal path, not an error. Cancelling throws <see cref="OperationCanceledException"/>.
/// </summary>
internal static class LoginResolver
{
    /// <param name="scheme">KeePassXC URL scheme for a blank entry, e.g. "rdp" looks for rdp://host.</param>
    /// <param name="defaultPort">The protocol's usual port, left out of that URL.</param>
    public static async Task<(string User, string Password)> ResolveAsync(
        SessionDefinition d, CredentialVault vault, FrameworkElement owner,
        Func<string, string, string?> promptForSecret, string scheme, int defaultPort)
    {
        var username = d.Username.Trim();
        string? password = null;

        if (d.PasswordSource == PasswordSource.KeePassXc)
        {
            var entry = d.KeePassEntry.Trim();
            if (entry.Length == 0)
            {
                entry = $"{scheme}://{d.Host.Trim()}" + (d.Port == defaultPort || d.Port == 0 ? "" : $":{d.Port}");
            }

            var login = await KeePassXcPasswords.LookupAsync(vault, entry, d.Host, d.Port, username);
            password = login.Password;
            if (username.Length == 0)
            {
                username = login.Login;
            }
        }
        else
        {
            password = vault.Get(d.Id);
        }

        if (username.Length == 0)
        {
            var dialog = new CredentialPromptWindow($"{d.Host}:{d.Port}", Environment.UserName)
            {
                Owner = Window.GetWindow(owner)
            };
            if (dialog.ShowDialog() != true)
            {
                throw new OperationCanceledException("Cancelled.");
            }

            username = dialog.Username;
            if (dialog.Password.Length > 0)
            {
                password = dialog.Password;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            password = promptForSecret($"Password for {username}", "password")
                       ?? throw new OperationCanceledException("Cancelled.");
        }

        return (username, password);
    }
}

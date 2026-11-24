using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using log4net;
using MissionPlanner.Mavlink;

namespace MissionPlanner.App.Core.ViewModels.Tools;

/// <summary>The MAVLink 2 signing keys and Mission Planner's rules for making one.</summary>
public static class SigningKeys
{
    /// <summary>The secret key for a passphrase: SHA-256 of its UTF-8 bytes (MAVAuthKeys.AddKey, MAVLinkInterface.setupSigning).</summary>
    public static byte[] FromPassphrase(string passphrase) => SHA256.HashData(Encoding.UTF8.GetBytes(passphrase));

    /// <summary>The AuthKeys form's password score and its verdict (40 or less weak, 60 or less good, above strong).</summary>
    public static (int Score, string Verdict) Strength(string input)
    {
        var len = input.Length;
        var score = len * 4;
        var n = input.Count(char.IsUpper);
        if (n > 0)
            score += (len - n) * 2;
        n = input.Count(char.IsLower);
        if (n > 0)
            score += (len - n) * 2;
        n = input.Count(char.IsNumber);
        if (n > 0)
            score += n * 4;
        n = input.Count(c => char.IsSymbol(c) || char.IsPunctuation(c));
        if (n > 0)
            score += n * 6;
        n = input.Skip(1).Take(len - 2).Count(c => char.IsSymbol(c) || char.IsPunctuation(c) || char.IsNumber(c));
        if (n > 0)
            score += n * 2;
        // letters only, numbers only
        if (input.Count(char.IsLetter) == len)
            score -= len;
        if (input.Count(char.IsNumber) == len)
            score -= len;

        return (score, score <= 40 ? "Weak" : score <= 60 ? "Good" : "Strong");
    }

    /// <summary>
    /// A copy of the key list with one key added or replaced. The link's reader looks through
    /// <see cref="MAVAuthKeys.Keys"/> on its own thread, so the list is swapped, never changed in place.
    /// </summary>
    public static MAVAuthKeys.AuthKeys With(MAVAuthKeys.AuthKeys keys, string name, byte[] key)
    {
        var copy = Copy(keys);
        copy[name] = new MAVAuthKeys.AuthKey { Name = name, Key = key };
        return copy;
    }

    public static MAVAuthKeys.AuthKeys Without(MAVAuthKeys.AuthKeys keys, string name)
    {
        var copy = Copy(keys);
        copy.Remove(name);
        return copy;
    }

    /// <summary>The name of the key the vehicle signs with, if it is in the list.</summary>
    public static string? NameOf(MAVAuthKeys.AuthKeys keys, byte[]? key) =>
        key == null ? null : keys.FirstOrDefault(k => k.Value.Key != null && k.Value.Key.AsSpan().SequenceEqual(key)).Key;

    private static MAVAuthKeys.AuthKeys Copy(MAVAuthKeys.AuthKeys keys)
    {
        var copy = new MAVAuthKeys.AuthKeys();
        foreach (var k in keys)
            copy[k.Key] = k.Value;
        return copy;
    }
}

/// <summary>One saved key.</summary>
public sealed partial class SigningKeyRow(string name, byte[] key) : ObservableObject
{
    public string Name { get; } = name;

    public byte[] Key { get; } = key;

    public string KeyText { get; } = Convert.ToBase64String(key);

    [ObservableProperty]
    private bool _inUse;
}

/// <summary>
/// Mission Planner's AuthKeys form (Ctrl+F > "signing"): the MAVLink 2 signing keys kept in
/// authkeys.xml, made from a passphrase; Use sends SETUP_SIGNING with a key to the vehicle and signs
/// the link with it, Disable clears the vehicle's key. Incoming packets are checked against every key.
/// </summary>
public sealed partial class SigningViewModel : ToolPageViewModel
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SigningViewModel));

    // MAVState.signingKey (the key the vehicle's packets matched) is internal to the library
    private static readonly System.Reflection.FieldInfo? SigningKeyField =
        typeof(MAVState).GetField("signingKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);

    private int _ticks;

    public SigningViewModel(AppServices services)
        : base("MAVLink signing",
            "MAVLink 2 signing keys: the vehicle accepts only packets signed with its key. Make a key from a passphrase, then Use sends it to the vehicle (SETUP_SIGNING) and signs this link with it.",
            services)
    {
        LoadKeys();
    }

    public ObservableCollection<SigningKeyRow> Keys { get; } = new();

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthText))]
    private string _passphrase = "";

    /// <summary>The passphrase's score as the AuthKeys form shows it.</summary>
    public string StrengthText
    {
        get
        {
            if (Passphrase.Length == 0)
                return "";
            var (score, verdict) = SigningKeys.Strength(Passphrase);
            return L.Format("Strength: {0} {1}", score, L.Tr(verdict));
        }
    }

    [ObservableProperty]
    private string _linkText = "";

    public bool IsEmpty => Keys.Count == 0;

    [RelayCommand]
    private void AddKey()
    {
        var name = NewName.Trim();
        if (name.Length == 0)
        {
            Report("Enter a name for the key.", true);
            return;
        }

        if (Passphrase.Length == 0)
        {
            Report("Enter the passphrase the key is made from.", true);
            return;
        }

        var (score, verdict) = SigningKeys.Strength(Passphrase);
        MAVAuthKeys.Keys = SigningKeys.With(MAVAuthKeys.Keys, name, SigningKeys.FromPassphrase(Passphrase));
        if (!Save())
            return;
        NewName = "";
        Passphrase = "";
        LoadKeys();
        Report(score <= 40
            ? L.Format("Key {0} added. Passphrase strength {1}: WEAK - please pick a better passphrase.", name, score)
            : L.Format("Key {0} added (passphrase strength {1}: {2}).", name, score, L.Tr(verdict)), score <= 40);
    }

    [RelayCommand]
    private async Task RemoveAsync(SigningKeyRow? row)
    {
        if (row == null || !await Services.Dialogs.ConfirmAsync("Remove key",
                $"Remove the key {row.Name}? A vehicle that signs with it can then no longer be heard.", "Remove"))
            return;
        MAVAuthKeys.Keys = SigningKeys.Without(MAVAuthKeys.Keys, row.Name);
        if (Save())
            LoadKeys();
    }

    /// <summary>Sends the key to the vehicle and signs the link with it.</summary>
    [RelayCommand]
    private async Task UseAsync(SigningKeyRow? row)
    {
        if (row == null)
            return;
        var key = row.Key;
        if (await RunAsync("Sending the key " + row.Name, (mav, sysid, compid) => mav.setupSigning(sysid, compid, "", key),
                $"Signing with {row.Name}: the vehicle now accepts only packets signed with it.", "Could not set up signing."))
            UpdateLink();
    }

    [RelayCommand]
    private async Task DisableAsync()
    {
        if (!await Services.Dialogs.ConfirmAsync("Disable signing",
                "Clear the vehicle's signing key and stop signing this link?", "Disable signing"))
            return;
        // setupSigning returns the new signing state: false is success here
        await RunAsync("Disabling signing", (mav, sysid, compid) => !mav.setupSigning(sysid, compid, ""),
            "Signing disabled.", "Could not disable signing.");
        UpdateLink();
    }

    public override void Tick()
    {
        base.Tick();
        if (++_ticks % 4 == 0)
            UpdateLink();
    }

    public override void OnShown()
    {
        base.OnShown();
        LoadKeys();
        UpdateLink();
    }

    /// <summary>The AuthKeys form's status line: the key in use and the signed packets per second.</summary>
    private void UpdateLink()
    {
        if (!IsConnected)
        {
            LinkText = "";
            return;
        }

        var mav = Services.Session.Mav;
        var vehicle = Services.Session.Vehicle;
        var name = SigningKeys.NameOf(MAVAuthKeys.Keys, SigningKeyField?.GetValue(vehicle) as byte[]);
        foreach (var k in Keys)
            k.InUse = k.Name == name;
        LinkText = L.Format("Using key: {0}, signing: {1}, signed packets: {2}", name ?? L.Tr("None/Unknown"),
            vehicle.signing ? L.Tr("on") : L.Tr("off"), mav.Mavlink2Signed);
    }

    private void LoadKeys()
    {
        Keys.Clear();
        foreach (var k in MAVAuthKeys.Keys.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (k.Value.Key != null)
                Keys.Add(new SigningKeyRow(k.Key, k.Value.Key));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Save()
    {
        try
        {
            MAVAuthKeys.Save();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("save keys", ex);
            Report("Could not save the keys: " + ex.Message, true);
            return false;
        }
    }
}

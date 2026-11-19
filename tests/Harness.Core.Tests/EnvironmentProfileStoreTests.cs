using System.Security.Cryptography;
using System.Text;
using Harness.Models;
using Harness.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Harness.Core.Tests;

[TestClass]
public sealed class EnvironmentProfileStoreTests
{
    private string _root = string.Empty;
    private EnvironmentProfileStore _store = null!;

    [TestInitialize]
    public void Initialize()
    {
        _root = Path.Combine(Path.GetTempPath(), $"harness-profile-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new EnvironmentProfileStore(_root);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Directory.Delete(_root, recursive: true);
    }

    [TestMethod]
    public void ExistingStableConfigurationIdMigratesLegacyLoginWithoutChangingContents()
    {
        var environment = CreateEnvironment("already-persisted-id", "Production");
        var legacy = CreateLegacyProfile(environment.Name, "signed-in");
        var localStorage = Path.Combine(legacy, "Default", "Local Storage");
        Directory.CreateDirectory(localStorage);
        File.WriteAllBytes(Path.Combine(localStorage, "session"), [0, 7, 254, 255]);
        _store.RegisterEnvironments([environment]);

        var profile = _store.PrepareUserDataFolder(environment.Id);

        Assert.AreEqual("signed-in", ReadLogin(profile));
        CollectionAssert.AreEqual(new byte[] { 0, 7, 254, 255 }, File.ReadAllBytes(Path.Combine(profile, "Default", "Local Storage", "session")));
        Assert.IsFalse(Directory.Exists(legacy));
        Assert.AreEqual(profile, new EnvironmentProfileStore(_root).PrepareUserDataFolder(environment.Id));
    }

    [TestMethod]
    public void FirstMigrationAfterRenameUsesPersistedOriginalSourceAndLeavesRenamedOrphanAlone()
    {
        var environment = CreateEnvironment("old-id", "Original");
        var original = CreateLegacyProfile(environment.Name, "correct-login");
        var unrelated = CreateLegacyProfile("Renamed", "unrelated-login");
        _store.RegisterEnvironments([environment]);
        environment.Name = "Renamed";
        var restarted = new EnvironmentProfileStore(_root);
        restarted.RegisterEnvironments([environment]);

        var profile = restarted.PrepareUserDataFolder(environment.Id);

        Assert.AreEqual("correct-login", ReadLogin(profile));
        Assert.AreEqual("unrelated-login", ReadLogin(unrelated));
        Assert.IsFalse(Directory.Exists(original));
    }

    [TestMethod]
    public void StableRenameKeepsSameFolderAndLogin()
    {
        var environment = CreateEnvironment("stable-id", "Before");
        _store.RegisterEnvironments([environment]);
        var profile = _store.PrepareUserDataFolder(environment.Id);
        WriteLogin(profile, "current-login");
        environment.Name = "After";
        _store.RegisterEnvironments([environment]);

        Assert.AreEqual(profile, _store.PrepareUserDataFolder(environment.Id));
        Assert.AreEqual("current-login", ReadLogin(profile));
    }

    [TestMethod]
    public void NewEnvironmentWithReusedNameCannotAdoptLegacyOrStableLogin()
    {
        var original = CreateEnvironment("original-id", "Shared");
        CreateLegacyProfile(original.Name, "original-login");
        _store.RegisterEnvironments([original]);
        var originalProfile = _store.PrepareUserDataFolder(original.Id);
        var orphan = CreateLegacyProfile(original.Name, "orphan-login");
        var replacement = CreateEnvironment("replacement-id", original.Name);
        var restarted = new EnvironmentProfileStore(_root);
        restarted.RegisterEnvironments([replacement]);

        var replacementProfile = restarted.PrepareUserDataFolder(replacement.Id);

        Assert.AreNotEqual(originalProfile, replacementProfile);
        Assert.IsFalse(File.Exists(LoginPath(replacementProfile)));
        Assert.AreEqual("original-login", ReadLogin(originalProfile));
        Assert.AreEqual("orphan-login", ReadLogin(orphan));
    }

    [TestMethod]
    public void NewEnvironmentCannotAdoptOrphanThatPredatesUpgradeSnapshot()
    {
        var orphan = CreateLegacyProfile("Deleted", "orphan-login");
        _store.RegisterEnvironments([CreateEnvironment("existing-id", "Saved")]);
        var created = CreateEnvironment("new-id", "Deleted");
        _store.RegisterEnvironments([created]);

        var profile = _store.PrepareUserDataFolder(created.Id);
        _store.DeleteUserDataFolder(created.Id);

        Assert.IsFalse(Directory.Exists(profile));
        Assert.AreEqual("orphan-login", ReadLogin(orphan));
    }

    [TestMethod]
    public void AbsentLegacyAtRegistrationCannotBecomeEligibleAfterRename()
    {
        var environment = CreateEnvironment("existing-id", "Before");
        _store.RegisterEnvironments([environment]);
        environment.Name = "After";
        var previousName = CreateLegacyProfile("Before", "late-original");
        var renamed = CreateLegacyProfile("After", "late-target");
        _store.RegisterEnvironments([environment]);

        var profile = _store.PrepareUserDataFolder(environment.Id);

        Assert.IsFalse(File.Exists(LoginPath(profile)));
        Assert.AreEqual("late-original", ReadLogin(previousName));
        Assert.AreEqual("late-target", ReadLogin(renamed));
    }

    [TestMethod]
    public void StableDestinationCollisionPreservesBothSessionsAndFails()
    {
        var environment = CreateEnvironment("existing-id", "Production");
        var legacy = CreateLegacyProfile(environment.Name, "legacy-login");
        _store.RegisterEnvironments([environment]);
        var target = _store.GetUserDataFolder(environment.Id);
        WriteLogin(target, "unknown-login");

        Assert.ThrowsExactly<IOException>(() => _store.PrepareUserDataFolder(environment.Id));
        Assert.ThrowsExactly<IOException>(() => _store.DeleteUserDataFolder(environment.Id));
        Assert.AreEqual("legacy-login", ReadLogin(legacy));
        Assert.AreEqual("unknown-login", ReadLogin(target));
    }

    [TestMethod]
    public void LockedLegacyMigrationFailsWithoutFallingBackAndCanBeRetried()
    {
        var environment = CreateEnvironment("existing-id", "Production");
        var legacy = CreateLegacyProfile(environment.Name, "locked-login");
        _store.RegisterEnvironments([environment]);
        using (File.Open(LoginPath(legacy), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => _store.PrepareUserDataFolder(environment.Id));
            Assert.IsFalse(Directory.Exists(_store.GetUserDataFolder(environment.Id)));
        }

        Assert.AreEqual("locked-login", ReadLogin(_store.PrepareUserDataFolder(environment.Id)));
    }

    [TestMethod]
    public void ClearingBeforeFirstMigrationAfterRenameDeletesOnlyRecordedSource()
    {
        var environment = CreateEnvironment("existing-id", "Original");
        var original = CreateLegacyProfile(environment.Name, "clear-me");
        var unrelated = CreateLegacyProfile("Renamed", "keep-me");
        _store.RegisterEnvironments([environment]);
        environment.Name = "Renamed";
        _store.RegisterEnvironments([environment]);
        _store.DeleteUserDataFolder(environment.Id);

        Assert.IsFalse(Directory.Exists(original));
        Assert.AreEqual("keep-me", ReadLogin(unrelated));
        CreateLegacyProfile("Original", "must-not-return");
        var restarted = new EnvironmentProfileStore(_root);
        Assert.IsFalse(File.Exists(LoginPath(restarted.PrepareUserDataFolder(environment.Id))));
    }

    [TestMethod]
    public void ClearingStableProfileDoesNotDeleteOrReimportReusedLegacyName()
    {
        var environment = CreateEnvironment("existing-id", "Production");
        CreateLegacyProfile(environment.Name, "old-login");
        _store.RegisterEnvironments([environment]);
        var profile = _store.PrepareUserDataFolder(environment.Id);
        var orphan = CreateLegacyProfile(environment.Name, "unrelated-login");
        _store.DeleteUserDataFolder(environment.Id);
        _store.DeleteUserDataFolder(environment.Id);

        Assert.IsFalse(Directory.Exists(profile));
        Assert.AreEqual("unrelated-login", ReadLogin(orphan));
        Assert.IsFalse(File.Exists(LoginPath(_store.PrepareUserDataFolder(environment.Id))));
    }

    [TestMethod]
    public void LockedProfileClearReportsFailureAndRetryClearsSession()
    {
        var environment = CreateEnvironment("existing-id", "Production");
        _store.RegisterEnvironments([environment]);
        var profile = _store.PrepareUserDataFolder(environment.Id);
        WriteLogin(profile, "locked-login");
        using (File.Open(LoginPath(profile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => _store.DeleteUserDataFolder(environment.Id));
            File.Delete(Path.Combine(profile, ".harness-environment-id"));
            Assert.ThrowsExactly<IOException>(() => new EnvironmentProfileStore(_root).PrepareUserDataFolder(environment.Id));
        }

        new EnvironmentProfileStore(_root).DeleteUserDataFolder(environment.Id);
        Assert.IsFalse(Directory.Exists(profile));
    }

    [TestMethod]
    public void FailedRegistrationRetainsOriginalSnapshotBeforeRenameAndNewEnvironment()
    {
        var environment = CreateEnvironment("existing-id", "Original");
        var original = CreateLegacyProfile(environment.Name, "correct-login");
        var unrelated = CreateLegacyProfile("Renamed", "unrelated-login");
        var orphan = CreateLegacyProfile("Reused", "orphan-login");
        using (File.Open(Path.Combine(_root, "environment-profiles.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => _store.RegisterEnvironments([environment]));
        }

        environment.Name = "Renamed";
        var created = CreateEnvironment("new-id", "Reused");
        _store.RegisterEnvironments([environment, created]);

        Assert.AreEqual("correct-login", ReadLogin(_store.PrepareUserDataFolder(environment.Id)));
        Assert.IsFalse(Directory.Exists(original));
        Assert.IsFalse(File.Exists(LoginPath(_store.PrepareUserDataFolder(created.Id))));
        Assert.AreEqual("unrelated-login", ReadLogin(unrelated));
        Assert.AreEqual("orphan-login", ReadLogin(orphan));
    }

    [TestMethod]
    public void InitializationFinishesInterruptedClearInsteadOfRestoringPartialLogin()
    {
        var environment = CreateEnvironment("existing-id", "Production");
        _store.RegisterEnvironments([environment]);
        var profile = _store.PrepareUserDataFolder(environment.Id);
        WriteLogin(profile, "must-be-cleared");
        using (File.Open(LoginPath(profile), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => _store.DeleteUserDataFolder(environment.Id));
        }

        var restarted = new EnvironmentProfileStore(_root);
        Assert.AreEqual(profile, restarted.PrepareUserDataFolder(environment.Id));
        Assert.IsFalse(File.Exists(LoginPath(profile)));
    }

    [TestMethod]
    public void RegistrationRejectsAmbiguousLegacyOwnership()
    {
        var legacy = CreateLegacyProfile("Shared", "ambiguous");

        Assert.ThrowsExactly<IOException>(() => _store.RegisterEnvironments([
            CreateEnvironment("first-id", "Shared"),
            CreateEnvironment("second-id", "Shared"),
        ]));

        Assert.AreEqual("ambiguous", ReadLogin(legacy));
        Assert.IsFalse(File.Exists(Path.Combine(_root, "environment-profiles.json")));
    }

    [TestMethod]
    public void RegistryLockAndCorruptionFailWithoutAdoptingLegacyData()
    {
        var environment = CreateEnvironment("existing-id", "Production");
        var legacy = CreateLegacyProfile(environment.Name, "keep-me");
        _store.RegisterEnvironments([environment]);
        using (File.Open(Path.Combine(_root, "environment-profiles.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsExactly<IOException>(() => _store.PrepareUserDataFolder(environment.Id));
            Assert.ThrowsExactly<IOException>(() => _store.DeleteUserDataFolder(environment.Id));
        }
        File.WriteAllText(Path.Combine(_root, "environment-profiles.json"), "invalid-json");

        Assert.ThrowsExactly<System.Text.Json.JsonException>(() => _store.PrepareUserDataFolder(environment.Id));
        Assert.AreEqual("keep-me", ReadLogin(legacy));
    }

    [TestMethod]
    public void MissingIdentityCannotFallBackToDisplayName()
    {
        var legacy = CreateLegacyProfile("Production", "keep-me");

        Assert.ThrowsExactly<ArgumentException>(() => _store.GetUserDataFolder(""));
        Assert.ThrowsExactly<InvalidOperationException>(() => _store.PrepareUserDataFolder("unknown-id"));
        Assert.ThrowsExactly<InvalidOperationException>(() => _store.DeleteUserDataFolder("unknown-id"));
        Assert.AreEqual("keep-me", ReadLogin(legacy));
        Assert.AreNotEqual(_store.GetUserDataFolder("id"), _store.GetUserDataFolder("ID"));
        Assert.AreEqual(Path.Combine(_root, "Profiles"), Path.GetDirectoryName(_store.GetUserDataFolder("../../outside")));
    }

    private static EnvironmentConfig CreateEnvironment(string id, string name) => new()
    {
        Id = id,
        Name = name,
        GatewayUrl = "https://gateway.test",
    };

    private string CreateLegacyProfile(string name, string login)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        var folder = Path.Combine(_root, $"{name}_{hash}");
        WriteLogin(folder, login);
        return folder;
    }

    private static string LoginPath(string folder) => Path.Combine(folder, "Default", "Network", "Cookies");

    private static string ReadLogin(string folder) => File.ReadAllText(LoginPath(folder));

    private static void WriteLogin(string folder, string login)
    {
        var path = LoginPath(folder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, login);
    }
}

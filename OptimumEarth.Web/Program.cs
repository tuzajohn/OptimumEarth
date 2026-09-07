using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

// Razor Pages' default routing already gives us the brief's SEO-aligned
// structure with no extra configuration: Pages/Index.cshtml -> "/",
// Pages/Uganda.cshtml -> "/Uganda" (matches "/uganda" case-insensitively),
// and likewise for Zambia and Foundation.
builder.Services.AddRazorPages();

builder.Services.AddResponseCompression();

// Data Protection needs somewhere writable to persist its key ring, but
// where that is varies by host - a systemd StateDirectory, a mounted
// volume in a container, a plain folder for local dev - and any one of
// them can turn out to be unavailable (our own production /tmp turned out
// to be sandboxed read-only by systemd's ProtectSystem=strict). Rather
// than hardcode one path and crash the moment it's wrong for a given
// host, probe a short list of candidates for a writable one at startup
// and use the first that works. If genuinely none are writable, fall
// back to ephemeral (in-memory, not persisted) keys instead of crashing -
// the app then degrades gracefully (antiforgery tokens/cookies reset on
// restart) rather than throwing on every request that needs one.
var dataProtectionBuilder = builder.Services.AddDataProtection()
    .SetApplicationName("OptimumEarth.Web");

var keysDirectory = FindWritableKeysDirectory(builder.Environment.ContentRootPath);
if (keysDirectory is not null)
{
    dataProtectionBuilder.PersistKeysToFileSystem(keysDirectory);
}
else
{
    dataProtectionBuilder.UseEphemeralDataProtectionProvider();
}

var app = builder.Build();

if (keysDirectory is not null)
{
    app.Logger.LogInformation("Data Protection keys persisted to {KeysDirectory}", keysDirectory.FullName);
}
else
{
    app.Logger.LogWarning(
        "No writable location found for Data Protection keys; using ephemeral (in-memory) keys. " +
        "Antiforgery tokens and other protected data will not survive a process restart.");
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseResponseCompression();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();

// Tries, in order: a directory an orchestrator has already granted this
// process persistent writable storage for (systemd's StateDirectory=,
// which it also exports as $STATE_DIRECTORY - used automatically when
// set, never required), the app's own content root, then the OS temp
// directory. Returns the first that's actually writable, or null if none
// are - callers should treat null as "fall back to ephemeral keys",
// never as fatal.
static DirectoryInfo? FindWritableKeysDirectory(string contentRootPath)
{
    var candidateRoots = new List<string>();

    var stateDirectory = Environment.GetEnvironmentVariable("STATE_DIRECTORY");
    if (!string.IsNullOrEmpty(stateDirectory))
    {
        // systemd separates multiple StateDirectory= entries with ':'.
        candidateRoots.AddRange(stateDirectory.Split(':', StringSplitOptions.RemoveEmptyEntries));
    }

    candidateRoots.Add(contentRootPath);
    candidateRoots.Add(Path.GetTempPath());

    foreach (var root in candidateRoots)
    {
        var dir = new DirectoryInfo(Path.Combine(root, "keys"));
        if (TryEnsureWritable(dir))
        {
            return dir;
        }
    }

    return null;
}

static bool TryEnsureWritable(DirectoryInfo dir)
{
    try
    {
        dir.Create();
        var probePath = Path.Combine(dir.FullName, $".write-probe-{Guid.NewGuid():N}");
        File.WriteAllText(probePath, string.Empty);
        File.Delete(probePath);
        return true;
    }
    catch (Exception)
    {
        return false;
    }
}

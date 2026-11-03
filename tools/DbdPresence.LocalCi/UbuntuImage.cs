namespace DbdPresence.LocalCi;

/// <summary>
/// The Ubuntu 24.04 WSL image every Linux job starts from, pinned by file name, size and SHA-256 (security review 2026-10-30, M6). Nothing about it is read from the
/// network at run time: the list of Canonical's files (SHA256SUMS) is not signed in transit, so an attacker who could change it could also have changed the image. The weekly
/// rebuild starts from this same file and only runs <c>apt-get upgrade</c> in it (APT checks the signed Ubuntu archive itself). A newer point release is adopted by changing
/// these constants in a reviewed commit.
/// </summary>
/// <remarks>
/// Taken from https://releases.ubuntu.com/noble/SHA256SUMS on 2026-10-29. That list's signature (SHA256SUMS.gpg, made 2026-09-15 with "Ubuntu CD Image Automatic Signing
/// Key (2012)", fingerprint 8439 38DF 228D 22F7 B374 2BC0 D94A A3F0 EFE2 1092) was checked with gpg: "Good signature". The file was then downloaded from the address below
/// (388,975,696 bytes, last modified 2026-09-08) and its SHA-256 computed independently: it equals the value in the list.
/// </remarks>
public sealed record UbuntuImage(string FileName, string Sha256, Uri Address, long SizeBytes)
{
    /// <summary>The image this version of the program uses.</summary>
    public static UbuntuImage Pinned { get; } = new(
        "ubuntu-24.04.5-wsl-amd64.wsl",
        "bb415d824822c4b878125729af451a5d18fb13d1cf5cbed9a7393ad64ac6039e",
        new Uri("https://releases.ubuntu.com/noble/ubuntu-24.04.5-wsl-amd64.wsl"),
        388_975_696);
}

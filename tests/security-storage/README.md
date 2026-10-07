# Local metadata and destructive-reset checks

This independent console project compiles production LocalStore, EncryptedLocalFile, AppLockService, GameorderDataContext and the native Windows DPAPI implementation. Default test mode never starts Steam, loads Steam native DLLs, connects to a network, or reads real account files. All account names, passwords, notes, groups, records, portraits and backups in its fixtures are synthetic.

```powershell
dotnet build .\tests\security-storage\SecurityStorage.csproj -c Release
dotnet .\tests\security-storage\bin\Release\net9.0-windows\SecurityStorage.dll
```

Requires Windows and .NET 9. The checks exercise actual CurrentUser DPAPI and password-bound AES-GCM, unicode/history round trips, validated legacy migration, bounded files, tamper and malformed input, wrong-password and missing-configuration fail-closed behavior, stale-writer rejection, enable/change/disable migrations and injected commit failures. They also cover isolated destructive reset, locked-file failure, ownership markers, a synthetic Steam-path boundary and a synthetic directory junction. Portrait checks validate three distinct 64x64 PNG files, their chunk CRCs and decompressed RGBA data. A successful explicit-import marker removal is preserved after reopening the context.

The temporary-write boundary is inspected before a deliberately failed commit: the temporary file contains ciphertext, the original is unchanged, and cleanup removes the temporary file. A rollback from a legacy plaintext file writes equivalent DPAPI-protected content, never a plaintext rollback file. No old metadata or security backup is created by the protection transitions. If rollback itself fails, the store remains blocked rather than allowing a later overwrite.

Results are written to verification/security-storage.json in the release work root. Synthetic GUID fixture directories are created only below this project's output directory and removed after each check. No second Windows user is created or switched; the report explicitly records that this cross-user case was not tested.

Program.cs.txt and PasswordStorageTests.cs.txt are compiled explicitly to avoid a second entry point in the parent core test project. The parent project excludes security-storage/**/*.cs, including this child project's generated obj files, from its default compile glob.

Two optional modes require an explicitly selected target file and are read-only. They never call LocalStore.Load, migrate data, or write decrypted data:

```powershell
# Suitable for either format; reads only envelope identity, length, version and ciphertext hash.
dotnet .\tests\security-storage\bin\Release\net9.0-windows\SecurityStorage.dll --file-status 'C:\explicit\accounts.json'

# DPAPI format only; prints counts and SHA-256 of decrypted UTF-8 JSON, never account values.
dotnet .\tests\security-storage\bin\Release\net9.0-windows\SecurityStorage.dll --live-status 'C:\explicit\accounts.json'
```

The header-only mode reports recognition, not successful integrity validation. The DPAPI-only mode rejects password-bound or plaintext files and has no password argument. Capture status JSON in a safe verification report only after deliberately selecting the path. Standard UTF-8 migration preserves the original JSON bytes, including unknown fields; BOM/UTF-16 encodings are normalized to UTF-8, preserving parsed values while changing the byte hash.

When application lock is disabled, metadata uses the GORDERLD version-1 envelope and CurrentUser DPAPI. When enabled, metadata uses GORDERPW version 1, a random 256-bit data key, AES-GCM with a 96-bit random nonce and 128-bit authentication tag. Configuration stores the data key wrapped with a PBKDF2-SHA256-derived password key (600,000 iterations, random 256-bit salt) and AES-GCM. The verifier is a purpose-specific HMAC, not the usable wrapping key. Configuration itself is additionally protected with CurrentUser DPAPI. Changing the password replaces the salt/wrapping material while preserving the metadata key and data. Existing 1.9.0 locked configuration is upgraded only after the correct password is verified. Deleting security.golock does not unlock password-bound metadata or permit saving over it.

Maximum JSON size is 8 MiB; its envelope is bounded to 8 MiB plus 64 KiB. UTF-8/key buffers and native DPAPI buffers are cleared when work completes. Parsed managed strings remain necessary while the app uses unlocked data. Public avatar PNGs and public account identity are not private metadata and are not password-encrypted. This does not protect an unlocked app from malicious code running as the same Windows user, and it does not encrypt or modify Steam's own credentials.

GameorderDataContext.CreateTest uses a dedicated marked ResetTest root. Its public synthetic password is 12345678. Explicit development roots are limited to H:\Documents\Playground; nonempty unmarked roots, reparse paths and Steam installation ancestry are rejected. First initialization creates synthetic data and starts locked. Subsequent creation preserves the current fixture; after reset it stays empty until explicitly rebuilt. Reset deletes only owned account/config files, matching GUID temporary files, avatar directories and own backups. It writes an empty DPAPI library with AutoImportSteamAccounts=false and keeps an import-suppression marker until a successful explicit import. Unrelated root files are preserved. Test fixtures never instantiate the production context.

# Code signing

WindowTabs ships unsigned. This note records why that matters, what the
options are, and what the build already does about it.

## Why it matters

An unsigned binary has no publisher identity, so it starts every download
with no reputation. Two things follow:

- **SmartScreen** warns on it until enough people have run it unharmed, and
  the counter resets with every new release.
- **Defender's machine-learning classifiers** have nothing but behaviour to
  judge it on. That is how an installer doing ordinary file maintenance came
  to be reported as `Trojan:Script/Wacatac.H!ml`, and how the executable
  extracted from an MSI was once quarantined as `Trojan:Win32/Bearfoos.A!ml`.

Removing the embedded PowerShell from the MSI takes away the strongest single
trigger. It does not give the package an identity. Only a signature does.

## What the build already does

`build_release.bat` signs both the executable and the MSI when two variables
are set, and skips the step entirely when they are not - so it is a no-op
until a certificate exists:

```
set WT_SIGN_TOOL=C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64\signtool.exe
set WT_SIGN_ARGS=/fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /n "Satoshi Yamamoto"
```

The executable is signed before it is copied into either the ZIP or the MSI,
so both carry the same signed binary; the MSI is signed after its contents
have been verified, so nothing modifies the package afterwards. Both are
verified with `signtool verify /pa` and the build fails if either check does.

`/tr` is not optional. Without a timestamp the signature stops verifying the
day the certificate expires, and every build already shipped becomes unsigned
again.

## Getting a certificate

Since June 2023 every publicly trusted code-signing certificate must have its
private key on a hardware token or in a cloud HSM. That rules out the old
"download a .pfx and keep it in the repo" arrangement, and it is why the
options below differ mainly in who holds the key.

| Option | Cost | Key custody | Notes |
|---|---|---|---|
| **SignPath Foundation** | free for OSS | SignPath's HSM | Signing runs in their service, driven from CI. Requires an application and an OSS licence that qualifies. The best fit for this project. |
| **Certum Open Source** | ~USD 30-90/yr | hardware token they ship | Cheapest certificate an individual can hold. Identity verification needed; the token must be plugged in for every release build. |
| **Azure Trusted Signing** | ~USD 10/mo | Microsoft's HSM | Cleanest to automate, but requires a verifiable legal entity with three years of history. A sole developer usually cannot qualify. |

An OV certificate does not grant instant SmartScreen reputation - that
accrues over downloads, but it accrues to the *publisher* rather than to each
individual file, so it survives new releases. An EV certificate does grant it
immediately, at several times the price.

## Order of work

1. Remove the script from the MSI. **Done** - the built MSI contains no
   `powershell`, `EncodedCommand`, `VBScript` or `JScript` string.
2. Report the released MSI to Microsoft as a false positive, so users on the
   current build stop being blocked.
3. Apply for a certificate and set `WT_SIGN_TOOL` / `WT_SIGN_ARGS`.

Signing does not always clear a machine-learning verdict on its own, which is
why step 1 comes first.

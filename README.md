# TeamsEchoBot — Complete Run & Test Guide

## Project Structure

```
TeamsEchoBot/
├── Controllers/
│   ├── CallingController.cs     ← Teams callback endpoint (/api/calling)
│   └── JoinCallController.cs    ← Postman trigger (/api/joinCall)
├── Media/
│   ├── CallHandler.cs           ← Per-call audio lifecycle manager
│   ├── AudioProcessor.cs        ← PCM buffering + silence detection
│   └── MediaPlatformExtensions.cs
├── Models/
│   └── BotConfiguration.cs     ← Config POCOs + JoinCallRequest
├── Services/
│   ├── BotService.cs           ← Graph Communications client + call joining
│   ├── AuthenticationProvider.cs ← AAD token acquisition
│   └── SpeechService.cs        ← Azure STT + TTS
├── appsettings.json            ← ⚠️ FILL THIS IN BEFORE RUNNING
├── Program.cs                  ← Startup, Kestrel HTTPS config
├── TeamsEchoBot.csproj
└── deploy.bat                  ← Build + copy helper
```

---

## Step 1 — Fill in appsettings.json

Open `appsettings.json` and replace ALL placeholder values:

| Field | Where to get it |
|---|---|
| `AadAppId` | AAD App Registration → Overview → Application (client) ID |
| `AadAppSecret` | AAD App Registration → Certificates & secrets → Value |
| `AadTenantId` | AAD App Registration → Overview → Directory (tenant) ID |
| `ServiceDnsName` | Your VM's DNS name (without https://) |
| `CallbackUri` | `https://YOUR_DNS/api/calling` |
| `MediaPort` | `8445` (or whatever port you opened in NSG) |
| `CertThumbprint` | Run `Get-ChildItem Cert:\LocalMachine\My` on the VM → copy the thumbprint of your win-acme cert |
| `Speech.Key` | Azure Speech resource → Keys and Endpoint → KEY 1 |
| `Speech.Region` | Azure Speech resource → Keys and Endpoint → Location (e.g., `eastus`) |

---

## Step 2 — Build & Publish (Local Machine)

```powershell
# In the project root on your local machine
dotnet restore
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

---

## Step 3 — Copy to VM

Use RDP file copy or SCP to copy the entire `./publish/` folder to `C:\TeamsEchoBot\` on the VM.

**Via RDP (easiest):**
1. Open RDP connection to your VM
2. In the RDP toolbar → click the folder icon to access local drives
3. Copy `publish/*` → paste into `C:\TeamsEchoBot\` on the VM

---

## Step 4 — Run on the VM

Open PowerShell as Administrator on the VM:

```powershell
cd C:\TeamsEchoBot

# Run directly (good for first-time testing — shows console logs live)
.\TeamsEchoBot.exe
```

**Expected startup output:**
```
[HH:mm:ss INF] Starting TeamsEchoBot...
[HH:mm:ss INF] Kestrel: bound HTTPS on port 443 using cert subject: CN=yourbot.eastus.cloudapp.azure.com
[HH:mm:ss INF] MediaPlatform initialized. FQDN: yourbot.eastus.cloudapp.azure.com, Port: 8445
[HH:mm:ss INF] BotService initialized. Graph Communications Client ready.
[HH:mm:ss INF] TeamsEchoBot running. Webhook: https://yourbot.eastus.cloudapp.azure.com/api/calling
[HH:mm:ss INF] Waiting for Postman POST to /api/joinCall to start a session...
```

If you see errors instead, see the Troubleshooting section below.

---

## Step 5 — Verify HTTPS with Health Check (Postman or Browser)

**Before testing the bot**, confirm the HTTPS endpoint is reachable:

```
GET https://YOUR_DNS/api/calling/health
```

Expected response `200 OK`:
```json
{
  "status": "running",
  "bot": "TeamsEchoBot",
  "timestamp": "2024-..."
}
```

✅ If this returns 200 → DNS, port 443, TLS, and Kestrel are all correct.  
❌ If this times out → Port 443 not reachable (NSG or Windows Firewall issue).  
❌ If you get a TLS error → Certificate mismatch (DNS name in cert doesn't match URL).

---

## Step 6 — Join a Meeting via Postman

1. Start or schedule a Teams meeting
2. Copy the meeting join URL (right-click → Copy link in Teams)
3. In Postman:

```
POST https://YOUR_DNS/api/joinCall
Content-Type: application/json

{
    "joinUrl": "https://teams.microsoft.com/l/meetup-join/..."
}
```

Expected response `200 OK`:
```json
{
  "callId": "some-guid-here",
  "message": "Bot is joining the meeting...",
  "nextStep": "Speak into the meeting..."
}
```

Watch the VM console — you should see within a few seconds:
```
[HH:mm:ss INF] Attempting to join Teams meeting: https://teams.microsoft.com/...
[HH:mm:ss INF] Join request accepted. Call ID: abc-123
[HH:mm:ss INF] Teams notification received at /api/calling from 52.xx.xx.xx
[HH:mm:ss INF] Call state changed → Establishing
[HH:mm:ss INF] Call state changed → Established
[HH:mm:ss INF] AudioSocket subscribed. Ready to receive audio.
```
  
---

## Step 7 — Test the Echo

1. In the Teams meeting, speak a sentence clearly
2. Stop speaking and wait ~1 second (silence detection window)
3. Watch the logs for:
```
[HH:mm:ss INF] Speech detected (RMS: 1234). Buffering utterance...
[HH:mm:ss INF] Silence detected after 45 speech frames. Flushing utterance to STT...
[HH:mm:ss INF] Sending 28800 bytes to Azure STT...
[HH:mm:ss INF] STT transcript: "Hello, can you hear me?"
[HH:mm:ss INF] TTS synthesized 44100 bytes for "Hello, can you hear me?"
[HH:mm:ss INF] TTS audio sent to call (44100 bytes, 68 frames)
[HH:mm:ss INF] Echo complete: "Hello, can you hear me?"
```
4. You should hear the bot speak your words back in the Teams meeting ✅

---

## Running as a Windows Service (Production)

```powershell
# Install as a service
sc.exe create TeamsEchoBot binPath= "C:\TeamsEchoBot\TeamsEchoBot.exe" start= auto DisplayName= "Teams Echo Bot"

# Start it
sc.exe start TeamsEchoBot

# Check status
sc.exe query TeamsEchoBot

# View logs (Serilog writes to C:\TeamsEchoBot\logs\)
Get-Content C:\TeamsEchoBot\logs\teamsechobot-*.txt -Tail 50 -Wait
```

---

## Troubleshooting

### ❌ Webhook never hit — no logs after joining

**This was the failure from last iteration.** Most common causes:

1. **URL mismatch**: The `CallbackUri` in appsettings.json must EXACTLY match the webhook URL set in Azure Bot → Channels → Teams → Calling. Including the `/api/calling` path.
2. **TLS cert mismatch**: The cert's CN/SAN must match the DNS name in the URL. Use `Get-ChildItem Cert:\LocalMachine\My` to verify the Subject on the VM.
3. **Port 443 blocked**: Do the health check GET first. If that fails, fix networking before testing the bot.
4. **netsh binding wrong**: Run `netsh http show sslcert` on the VM and confirm `0.0.0.0:443` is bound.

### ❌ Bot joins but no audio received

- Verify port 8445 is open in NSG (both TCP and UDP inbound rules)
- Verify port 8445 is open in Windows Firewall on the VM
- Check `CertThumbprint` matches the cert bound to port 443 — the media platform uses this for DTLS

### ❌ STT returns empty transcript

- Verify Speech Key and Region in appsettings.json
- Test STT in Azure Speech Studio first (Phase 4 checkpoint)
- Try lowering `RmsThreshold` in AudioProcessor.cs if audio is quiet (default: 500)

### ❌ App crashes on startup with cert error

- Run on VM: `Get-ChildItem Cert:\LocalMachine\My`
- Copy the exact Thumbprint (no spaces) of your win-acme cert into appsettings.json
- Thumbprint must be the cert for your DNS name, NOT the Azure CRP cert

### ❌ 403 Forbidden when calling /api/joinCall

- Check that all 6 Graph API permissions are granted with admin consent (green checkmarks)
- The bot must have `Calls.JoinGroupCall.All` and `Calls.AccessMedia.All`

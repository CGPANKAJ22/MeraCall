# 🖨️ MeraPrint — Printer + Scanner + Remote Desktop + File transfer (kahin se bhi)
**Built by Pankaj Jaiswal** — MeraCall wale tools aur Cloudflare raaste par bana.

Ek PC **Host** banta hai, jisse printer/scanner jude hain. Phone (APK) ya doosra PC (browser) **Client** banta hai.

| Kaam | Kya hota hai |
|---|---|
| 🖨️ **Print** | Phone/PC se file bhejo → Host ke printer par chhape. Copies, pages, color/B&W, dono taraf, kagaz size, disha |
| 📠 **Scan** | Host ke scanner se scan → photo seedhe phone/PC par (Save) |
| 🖥️ **Remote** | Host PC ki screen phone/PC par dekho aur chalao (mouse, keyboard, scroll) |
| 📤 **File bhejna** | Phone/PC se koi bhi file Host PC par → `MeraPrint-Share\Aayi hui files\<device naam>` |
| 📥 **Download** | Host PC ke `MeraPrint-Share` folder se file phone/PC par |
| 📶 **LAN** | Same WiFi par seedha, sabse tez |
| 🌐 **Internet** | Bahar se bhi — Cloudflare ka free raasta (MeraCall Daakghar jaisa) |

## 👤 Login ID + Password (baar-baar ijazat nahi)
Admin panel → **Login ID / Password** → ID + password daal kar **Login banao** → usi line me Print / Scan / Remote / File / Download ✅❓⛔ chuno.
- Client (browser / phone app / PC client) me Host ID + **Login ID + Password** → Jodo.
- Kisi bhi PC / phone / link (LAN, internet, pakka link) se login karo — **wahi ijazat**, naya device nahi banta.
- Password badlo → us ID ke sab purane login band. 5 galat password → 10 minute band.
- PIN wala tarika bhi chalta hai (Login ID na ho to).

## 🔒 Suraksha — kya kahan rehta hai
| Cheez | Kahan | Doosre Host / user ke paas? |
|---|---|---|
| GitHub token, Firebase secret | sirf aapke PC (`D:\call apk\meracall-config.json`) | ❌ kabhi nahi |
| Update signature chabi | `publish\signing\update-private.pem` — **AES-256 + aapke password se band** | ❌ |
| Client PC ka token | Windows DPAPI se band (file copy karke doosre PC par nahi khulti) | ❌ |
| Host par client passwords | scrypt hash (asli password kahin likha nahi) | ❌ |
| Cloudflare | koi account nahi (free quick tunnel) — churane ko kuch nahi | — |
| Doosron ko | sirf code + khule pate (Firebase URL, repo naam) + public key | ✅ koi secret nahi |
- Ek hi cheez ko "100 baar encrypt" karne se suraksha nahi badhti — asli suraksha: chabi doosre ke paas na jaye (yahi kiya hai) + signature (nakli update na lage).
- LAN par (same WiFi) data seedha jata hai — dukaan / ghar ka WiFi bharose ka rakhiye. Internet par Cloudflare ka HTTPS.

## Ijazat — har client ke liye aap chuno (Host par)
Admin panel `http://localhost:8791` (sirf Host PC par khulta hai) me har jude device ke aage teen dabbe:

Har device ke aage **5 dabbe**: 🖨️ Print • 📠 Scan • 🖥️ Remote • 📤 File bhejna • 📥 Download — har ek me:

| | Matlab |
|---|---|
| ✅ **Seedha** | bina approval — client khud kar lega |
| ❓ **Poochho** | har baar Host PC par **"Allow?"** popup (ya admin panel me Allow) |
| ⛔ **Mana** | bilkul nahi |

Jaise: *Mera phone* = sab ✅, *Dukaan wala PC* = Print ✅ + Remote ❓, *Mehmaan ka phone* = Print ❓ + baaki ⛔. **Saare options sirf Host ke paas** — client kuch nahi badal sakta.

- **Naye device** wali line: naya device jodte hi use kya mile (shuru me sab ❓).
- Popup 90 second me jawab na mile to apne aap **Mana**.
- Remote chalte waqt Host PC ki screen par upar laal patti: **"● Remote chal raha hai"** + **Band karo** button.
- 🗑️ se device hatao → wo turant kat jata hai, dobara PIN ke bina nahi jud sakta.

---

## 1. Host PC par (ek baar)
1. `START-HOST.bat` double-click.
   - Pehli baar ye khud download karta hai: **SumatraPDF** (PDF/photo print, free) aur **cloudflared** (internet, free — MeraCall wala mil gaya to wahi).
   - Remote ke liye `helper.exe` Windows ke apne C# compiler se ban jata hai — kuch install nahi.
2. Windows Firewall poochhe → **Private networks → Allow**.
3. Browser me admin panel khulega: **Host ID** (jaise `ABCD-EF23-GH45`) aur **PIN** (6 ank) dikhega.
4. PC chalu hote hi apne aap chale: `ADD-TO-STARTUP.bat` ek baar chalaiye.
5. Window khuli rakhein (band = Host band).

**Firebase (Host ID se dhoondhna):** Host aapki MeraCall config (`D:\call apk\meracall-config.json`) se Firebase use karta hai, taaki phone sirf **Host ID** se PC dhoondh le (internet ka pata har restart par badalta hai).
→ Firebase → Realtime Database → **Rules** me `firebase-rules-NEW.json` ka text paste → **Publish** (isme sirf naya `printHosts` hissa juda hai, baaki MeraCall jaisa hi).

## 2. Phone APK
1. `BUILD-APK.bat` → `out\MeraPrint.apk` (MeraCall wale Android tools se, Gradle nahi).
   - Pehli baar `android\signing` me apni key banti hai — **ise sambhal kar rakhiye** (pen drive me copy). Isi se agla update purane ke upar install hota hai.
2. APK phone par bhejo → install.
3. App → **➕ Naya PC jodo** → Host ID + PIN → **Jodo**.
4. Kisi bhi app (Gallery, WhatsApp, File manager) me file → **Share → MeraPrint (PC par Print)**.

## 🖨️ Har software me Host ke printer (Aadhaar client, Word, PDF, browser, Tally...)
### 💻 Client PC par
1. Client PC ke browser me Host kholo (jaise `http://192.168.1.5:8790`) → jodo → **Print** tab → **⬇️ MeraPrint Client Setup download karo**.
   (Ya pen drive se `client-pc\MeraPrint-Client-Setup.bat` le jao — tab Host ka pata khud daalna hoga.)
2. Setup file chalao → window me Host ka pata + **PIN** → **Jodo** → Windows **"Yes"** (printers banane ki ijazat).
3. Ab us PC ke **har software** me Print dabao → printer list me **"HP LaserJet (MeraPrint - PK)"** jaisa naam → chuno → Print.
   Print PDF banakar seedha Host PC par jata hai aur wahan ke asli printer se nikalta hai — LAN ho ya internet.
- Ghadi ke paas MeraPrint icon (right-click): *Black & White*, *Dono taraf*, *Printers dobara banao* (Host par naya printer laga ho to), *Doosre Host se jodo*, *Band karo*.
- PC chalu hote hi apne aap chalta hai. Host band ho to print sambhal ke rakhta hai, Host milte hi bhej deta hai.
- Host par ye ek alag device dikhta hai (jaise "AADHAR-PC (Printer)") — iski Print ijazat ✅/❓/⛔ aap chuno.
- Zaroorat: Windows 10/11 ("Microsoft Print to PDF" — Windows me pehle se; band ho to setup khud chalu karta hai).
- Software ke print dialog ke color / dono-taraf options yahan nahi jaate — wo tray icon se chuniye.

### 📁 Client PC se file bhejna / lena (MeraPrint Client se)
- Ghadi ke paas MeraPrint icon par **double-click** (ya right-click → *Files*) → Host ke `MeraPrint-Share` folder ki list.
  - **File bhejo** ya files ko window par kheench kar chhodo → Host: `MeraPrint-Share\Aayi hui files\<PC naam>`
  - file chuno → **Download** → is PC ke `Downloads\MeraPrint` me (Explorer khud khulta hai).
- Kisi bhi file par right-click → **Send to → MeraPrint Host (pk)** → seedha Host par.
- Host admin panel me is PC ki **📤 File bhejna / 📥 Download** ijazat ✅ karoge to har baar "Allow?" nahi aayega.

### 📠 NAPS2 (scan software) me Host ka scanner
MeraPrint Client chalu ho to client PC par Host ka scanner ek **ESCL network scanner** ban jata hai — LAN ho ya internet.
1. Client PC par naya `MeraPrint-Client-Setup.bat` ek baar chalao (purana band karke naya chalu ho jayega).
2. Ghadi ke paas icon → **NAPS2 ke liye scanner chuno** (Host par ek se zyada scanner hon to).
3. NAPS2 → Profile banao/Edit → **Driver: ESCL** → **MeraPrint Scanner (pk)** chuno.
   Na dikhe to **Manual IP**: `127.0.0.1`, Port `9801`.
4. NAPS2 me Scan → Host ke scanner par scan → page NAPS2 me. (Host par is client ki **Scan** ijazat ✅/❓.)
- Ek baar me ek page (flatbed). Agla page = NAPS2 me dobara Scan.
- Suraksha: ye scanner sirf usi PC ke andar (127.0.0.1) khulta hai — LAN ka koi aur is raaste scan nahi kar sakta.

### 📱 Phone par
1. MeraPrint app → Host → **Print** tab → **⚙️ Print service chalu karo** → **MeraPrint (PC ke printer) → ON** (ek baar).
2. Ab phone ke **har app** (Gallery, PDF, WhatsApp, Chrome, Aadhaar...) me ⋮ → **Print** → printer chuno → Host ke printer dikhenge (copies, color, dono taraf, kagaz — sab yahin se).

## 🌍 Koi bhi apna PC Host banaye + aapke update sab jagah
### Naye log (unka apna Host)
Unhe ye link bhejiye: **https://cgpankaj22.github.io/MeraCall/meraprint/host.html** → *MeraPrint Host Install* download → chalao.
- `C:\MeraPrint-Host` me install, Node.js na ho to khud lagta hai, Desktop par "MeraPrint Host".
- Unka **apna** Host ID, PIN, devices, share folder — aapka koi data unke paas nahi, unka aapke paas nahi.
- Aapka GitHub token / Firebase secret kabhi unke PC par **nahi** jata (sirf khule pate: `public.json`).
- Unke Host apna internet pata aapke Firebase (`printHosts/<ID>/pub`) me likhte hain — har Host ki apni chabi, koi doosra badal nahi sakta.
  → Iske liye Firebase Rules me **`firebase-rules-NEW.json`** paste karke Publish karna **zaroori** hai (MeraCall ke rules waise hi hain).

### Aap update karo → sab jagah update
1. (Phone app badla ho to pehle `BUILD-APK.bat`)
2. **`PUBLISH-HOST-UPDATE.bat`** → "kya naya hai" likho → GitHub par ek commit (signature ke saath).
3. Har Host 30 minute me jaanch kar khud lagata hai aur dobara chalu hota hai → unke **PC client** (6 ghante me) aur **phone app** ("Naya update") unke Host se update.
- Signature ki chabi: `publish\signing\update-private.pem` — **sirf aapke PC par, pen drive me copy rakhiye, kisi ko mat dijiye**. Ye kho gayi to purane Host naye update nahi lenge.
- Aapka apna Host (jisme GitHub token hai) khud update nahi leta — update wahi se nikalta hai.
- Galat signature / badli hui file wala update koi Host nahi lagata.

## 🔗 Internet link badle to bhi apne aap judna
Host har restart par naya `https://....trycloudflare.com` link leta hai. Host ye naya link **GitHub** (MeraCall wala repo, file `meraprint/<HostID>.json`) par likh deta hai — Firebase ki zaroorat nahi.
- Phone app, browser page, PC client, NAPS2, phone ki Print service — sab purana link band hote hi naya link khud dhoondh kar jud jaate hain.
- **Pakka link** (kabhi nahi badlega) — admin panel me dikhta hai: `https://cgpankaj22.github.io/MeraCall/meraprint/#<HostID>` → hamesha Host ke abhi wale link par le jata hai. Ise bookmark / WhatsApp karo.
- Naya link GitHub par 1-5 minute me sab jagah pahunchta hai.
- MeraCall app / update par iska koi asar nahi — sirf `meraprint/` folder me alag file.
- Ye file public hai (Host ka naam + pata) — PIN, token, files kabhi nahi. Bina PIN koi nahi jud sakta.

## 🔄 Update — MeraPrint aur MeraCall bilkul alag
| | MeraCall | MeraPrint |
|---|---|---|
| Phone par | alag app (`com.cgpankaj22.meracall`) | alag app (`com.cgpankaj22.meraprint`) |
| Signing key | `D:\call apk\android\signing` | `D:\printer test\android\signing` |
| Update kaise | `D:\call apk\PUBLISH-UPDATE.bat` → GitHub se | `D:\printer test\BUILD-APK.bat` → **Host PC se** |
| Kis par asar | sirf MeraCall | sirf MeraPrint |

**MeraPrint update karna:**
1. Code badla → `BUILD-APK.bat` → "kya naya hai" likho (ya Enter) → `out\MeraPrint.apk` + `out\MeraPrint.json`.
2. Host chalu rakho. Phone ki MeraPrint app Host se judte hi hara dabba: **✨ Naya update → ⬇️ Update karo** → download → Install.
3. Pehli baar phone poochhega *"Is source se install"* → MeraPrint ke liye ON.
- Doosre PC (browser) ko update ki zaroorat nahi — wo hamesha Host ka taaza page kholta hai.
- Suraksha: phone sirf usi key se bana APK lagne deta hai — koi aur nakli APK upar install nahi ho sakta.

## 3. Doosra PC (client)
- Same WiFi: browser me admin panel me dikha LAN pata kholo (jaise `http://192.168.1.5:8790`) → PIN → Jodo.
- Bahar se: admin panel me dikha **Internet** pata (`https://....trycloudflare.com`) kholo → PIN → Jodo. (Ye pata Host restart par badalta hai — phone app Host ID se khud dhoondh leti hai.)
- Koi bhi PC Host bhi ban sakta hai: ye folder copy karke `START-HOST.bat`.

---

## Ek client se doosre client ko Remote?
Jis PC ko door se chalana hai, **wahi Host hota hai**. To agar *PC-1* se *PC-2* chalana ho: PC-2 par bhi ye folder copy karke `START-HOST.bat` chalao, aur PC-1 ko PC-2 ke Host ID + PIN se jodo. PC-2 ka admin panel tay karega PC-1 ko ✅/❓/⛔. (Phone ko door se chalana Android me sambhav nahi — sirf PC.)

## 📁 Share folder
- Default: `D:\printer test\MeraPrint-Share` (badalna ho to `host-config.json` me `shareFolder`).
- **Download:** jo file is folder me rakhoge, wahi clients ko dikhegi (iske bahar ka PC kabhi nahi dikhta).
- **File bhejna:** aayi files `Aayi hui files\<device naam>\` me. File 600 MB tak.
- Phone par download: *Download/MeraPrint* + notification.

## Kaunsi files print hoti hain
| File | Kaise |
|---|---|
| PDF, JPG, PNG, BMP, GIF, TIFF, WEBP, XPS, EPUB | SumatraPDF — saare options chalte hain |
| Word, Excel, PowerPoint, RTF, TXT, ODT, CSV | Pehle **PDF** banti hai (MS Office, warna LibreOffice), phir print — saare options |
| Baaki koi bhi | Windows ka apna "Print" (jis app se wo file khulti hai) |

Seema: ek file 150 MB tak (`host-config.json` me `maxUploadMB`). Print ke 10 minute baad file Host se mit jati hai. Scan 1 ghante baad.

## Remote Desktop chalana
- **Phone:** tap = click • lamba dabao = right-click • ungli kheencho = drag • do ungli = scroll • 🔍+ zoom, phir ✋ se screen sarkao • ⌨️ keyboard • **Keys** = Esc, Tab, Win, Ctrl+C/V, Alt+Tab, Task Manager...
- **PC browser:** mouse aur keyboard seedhe.
- Quality: *Halka* (internet / dheema net), *Theek*, *Saaf*, *Sabse saaf* (WiFi).
- ⚠️ Host PC **lock** ho ya **UAC (Haan/Nahi) popup** aaye to screen nahi dikhti — Windows ise rokta hai. Lock screen na aaye isliye Host PC par *Settings → Accounts → Sign-in options → Require sign-in: Never* rakh sakte hain (apni suraksha dekh kar).

## Suraksha
- Bina PIN koi nahi jud sakta. PIN ek baar use hote hi badal jata hai. 5 galat PIN → 10 minute pairing band.
- Har device ka apna lamba token (Host par sirf uska hash rakha jata hai).
- Admin panel sirf Host PC par (`127.0.0.1`), internet ya LAN se nahi khulta.
- LAN par data seedha jata hai (ghar/dukaan ka WiFi bharose ka ho). Internet par Cloudflare ka HTTPS.
- Firebase me sirf PC ka naam aur pata hota hai (Host ID jaanne wale ko) — PIN, token, files kabhi nahi.

## Files
```
START-HOST.bat        Host chalao
ADD-TO-STARTUP.bat    PC chalu hote hi Host chalu
BUILD-APK.bat         Phone APK banao / update  -> out\MeraPrint.apk (+ MeraPrint.json)
PUBLISH-HOST-UPDATE.bat  Naya code sab Host tak (sirf aapke PC par)
publish\              Update nikalne ka program + installer (sirf aapke PC par; signing\ me chabi)
host-config.json      (pehli baar banti hai) PC ka naam, port 8790, admin 8791, internet on/off
firebase-rules-NEW.json  Firebase Rules (MeraCall + printHosts)
host\host.js          Host server (Node.js, koi npm nahi)
host\helper.cs        Screen / mouse / keyboard / popup (Windows C#)
host\scan.ps1         Scanner (Windows WIA)
host\office2pdf.ps1   Word/Excel/PPT -> PDF
host\admin.html       Admin panel
host\data\            Jude devices (state.json) — kisi ko mat dena
MeraPrint-Share\       Download ke liye files + aayi hui files
web\index.html        Client screen (browser + APK dono me yahi)
client-pc\            PC client: MeraPrint-Client-Setup.bat (har software me Host printer)
android\              APK ka code
```

## Dikkat aaye to
| Dikkat | Kya karein |
|---|---|
| Phone par "PC nahi mila" | Host chalu? Same WiFi? Bahar ho to admin panel me Internet hara? Firebase rules paste kiye? |
| Print ❌ "SumatraPDF nahi mila" | `START-HOST.bat` dobara (download), ya portable SumatraPDF `host\tools\SumatraPDF.exe` naam se rakho |
| Scan me "Scanner nahi mila" | Scanner Windows ke *Fax and Scan* se chalta hai? Driver lagao |
| Remote me kaala screen | Host PC lock hai — unlock karo |
| "Remote / popup helper: band" | Admin log dekho — .NET Framework 4 (Windows me pehle se) chahiye |
| Phone se jodne par timeout | Firewall me Node.js ko Private network par Allow |

# Smart Download Manager (SDM)

دانلود منیجر هوشمند و متن‌باز برای ویندوز، با C# و .NET 8.
فایل‌ها را چندتکه و سریع دانلود می‌کند و بعد از پایان دانلود، خودش آن‌ها را در پوشهٔ درست می‌گذارد؛
مثلاً `Dark.S01E03.1080p.mkv` به `Series\Dark\Season 01` منتقل می‌شود.

*An open-source smart download manager for Windows (C# / .NET 8). Segmented downloads with resume,
and automatic sorting of finished files — TV episodes go to `Series\<Show>\Season NN`.*

## امکانات نسخهٔ فعلی (MVP)

- دانلود چندتکه با ۱، ۴، ۸، ۱۶ یا ۳۲ کانکشن برای هر دانلود (پیش‌فرض ۳۲)؛ اگر سرور تعداد کانکشن را محدود کند (خطای 429 یا 503)، برنامه خودش تعداد را کم می‌کند
- ادامهٔ دانلود (Resume) بعد از توقف، بسته شدن برنامه یا قطع اینترنت؛ وضعیت هر ۲ ثانیه در `.sdmstate` ذخیره می‌شود
- اگر فایل روی سرور عوض شده باشد (ETag / حجم / Last-Modified)، دانلود از اول شروع می‌شود تا فایل خراب نشود
- تلاش مجدد خودکار برای هر تکه با فاصلهٔ زمانی فزاینده
- محدودیت سرعت کلی (Token Bucket)، صف با تعداد دانلود همزمان قابل تنظیم، پروکسی HTTP و SOCKS5
- دسته‌بندی هوشمند به ترتیب: **موضوعات کاربر ← تشخیص سریال ← نوع فایل**
  - سریال: `S01E03`، `s1e3`، `1x03`، `Season 1 Episode 3`، `فصل ۱ قسمت ۳`، `قسمت ۷`
  - اگر پوشه‌ای برای سریال یا فصل با نام کمی متفاوت از قبل وجود داشته باشد (مثل `The Office (US)` یا `S2`)، از همان استفاده می‌شود
  - نوع فایل: پسوند، امضای ابتدای فایل (Magic Bytes) و MIME
- امکان بازگرداندن (Undo) هر انتقال
- **دستیار هوشمند:** با وارد کردن لینک، پنجره‌ای باز می‌شود که می‌گوید محتوا چیست (سریال، فیلم، موسیقی، نرم‌افزار و غیره) و کجا ذخیره شود
  - هوش مصنوعی قابل انتخاب: Claude، OpenAI، Gemini، Ollama (محلی و رایگان) یا هر سرور سازگار با OpenAI
  - هوش مصنوعی فقط وقتی تشخیص محلی مطمئن نیست صدا زده می‌شود (یا برای همهٔ دانلودها، در صورت انتخاب در تنظیمات). فقط نام فایل، آدرس و عنوان صفحه ارسال می‌شود.
  - نام رسمی، سال و نام قسمت از **TMDB** گرفته می‌شود
  - اتصال به **Emby** یا **Jellyfin**: فایل در کتابخانهٔ مناسب (TV Shows، Movies، Music) ذخیره می‌شود و بعد از دانلود، کتابخانه اسکن می‌شود
  - تیک «تغییر نام استاندارد» (مثل `Dark - S01E03 - Secrets.mkv`) یا نگه داشتن نام فعلی
  - گزینهٔ «همیشه این عنوان را اینجا ذخیره کن» که خودکار یک قانون می‌سازد
- رابط کاربری WPF به سه زبان فارسی (راست‌به‌چپ)، انگلیسی و آلمانی
- خط فرمان `sdm` برای تست و اسکریپت

## ساختار پروژه

```
src/SDM.Core     موتور دانلود، صف، تشخیص‌دهنده‌ها و انتقال فایل (بدون وابستگی به ویندوز)
src/SDM.App      رابط کاربری WPF
src/SDM.Cli      خط فرمان
tests/           تست‌های xUnit (شامل یک سرور HTTP محلی برای تست Range و Resume)
```

## ساخت و اجرا

پیش‌نیاز: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

```powershell
dotnet build SmartDownloadManager.sln
dotnet run --project src/SDM.App          # رابط کاربری (فقط ویندوز)
dotnet test tests/SDM.Core.Tests          # تست‌ها (ویندوز، لینوکس یا مک)
```

خط فرمان:

```powershell
dotnet run --project src/SDM.Cli -- get https://example.com/file.zip --out D:\Downloads
dotnet run --project src/SDM.Cli -- classify "Dark.S01E03.1080p.mkv" "Dune.Part.Two.2024.2160p.mkv"
```

در خط فرمان، `Ctrl+C` دانلود را متوقف می‌کند و پیشرفت را ذخیره می‌کند. اجرای دوبارهٔ همان دستور، دانلود را ادامه می‌دهد.

## تنظیمات

تنظیمات در `%AppData%\SDM\settings.json` ذخیره می‌شوند. نمونهٔ یک موضوع:

```json
{
  "DownloadDirectory": "D:\\Downloads",
  "SeriesDirectory": "D:\\Series",
  "Rules": [
    { "Name": "Dark", "Keywords": ["dark"], "TargetFolder": "D:\\Series\\Dark", "SeasonSubfolders": true },
    { "Name": "Dev", "Domains": ["github.com"], "TargetFolder": "D:\\Dev" }
  ],
  "MaxSegments": 32,
  "RenameToStandard": true,
  "Ai": { "Provider": "Claude", "ApiKey": "sk-ant-...", "Model": "", "BaseUrl": "" },
  "TmdbApiKey": "...",
  "MediaServer": { "Type": "Emby", "Url": "http://localhost:8096", "ApiKey": "...", "RefreshAfterDownload": true }
}
```

## نقشهٔ راه

- **فاز ۲:** اکستنشن مرورگر (Chrome / Edge / Firefox) با Native Messaging، زمان‌بندی، FTP، ذخیره‌سازی SQLite، نصاب‌کننده، System Tray و اعلان
- **فاز ۳:** دانلود استریم HLS/DASH، پشتیبانی Plex، نگاشت مسیر برای سرور رسانه روی NAS، رمزگذاری کلیدهای API با DPAPI

## محدودیت‌ها

- کلیدهای API فعلاً به‌صورت متن ساده در `settings.json` (داخل پروفایل کاربر) ذخیره می‌شوند.
- اگر Emby روی سیستم دیگری اجرا شود (مثلاً NAS)، مسیر کتابخانه‌هایش روی این کامپیوتر وجود ندارد و پیشنهاد به پوشه‌های خود برنامه برمی‌گردد.

- SDM محتوای محافظت‌شده با DRM (مثل Widevine، PlayReady و FairPlay) را دانلود نمی‌کند و چنین محتوایی را فقط تشخیص می‌دهد.
- مسئولیت رعایت قانون کپی‌رایت و شرایط استفادهٔ سایت‌ها با کاربر است.

## مجوز

[MIT](LICENSE)

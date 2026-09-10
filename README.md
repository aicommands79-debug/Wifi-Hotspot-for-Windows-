# Windows 11 Wi-Fi Hotspot & Web Giriş Portalı (Captive Portal)

Windows 11 üzerinde bilgisayarınızın internetini paylaşmanızı ve kullanıcıların **özel bir web giriş sayfası (Captive Portal)** üzerinden **kişiye özel kullanıcı adı ve şifreyle** bağlanmasını sağlayan modern masaüstü uygulaması.

---

## 🌟 Öne Çıkan Özellikler

### 1. Web Giriş Portalı (Captive Portal)
- Wi-Fi ağına bağlanan kullanıcılar tarayıcıyı açtıklarında modern, mobil uyumlu ve Türkçe tasarlanmış **Windows 11 Wi-Fi Giriş Portalı** ile karşılaşır.
- Kişi kendisine verilen kullanıcı adı ve şifreyi girerek "Giriş Yap" butonuna basar.
- Giriş yapmayan cihazlar internete erişemez ve doğrudan portal sayfasına yönlendirilir.

### 2. Kişiye Özel Kullanıcı Adı & Şifre (Bilet Yönetimi)
- **🎲 Tek Tıkla Rastgele Bilet Üretimi**: Admin panelinden tek tuşla rastgele kullanıcı adı ve şifre oluşturulabilir (`misafir_4812` / `728194`).
- **➕ Özel Kullanıcı Ekleme**: İstediğiniz kullanıcı adı ve şifreyi manuel olarak kaydedebilirsiniz.
- **🔒 Tek Cihaz Kuralı (Single Device Enforcement)**:
  - Bir kullanıcı adı ve şifre ile sisteme bir cihaz (telefon, tablet vb.) giriş yaptığında, o hesap o cihazın fiziksel donanımına (MAC/IP) kilitlenir.
  - Başka bir kişi aynı kullanıcı adı ve şifreyi girmeye kalkarsa sistem **"Bu hesap şu anda başka bir cihazda kullanımda! Aynı hesapla yalnızca 1 kişi bağlanabilir"** uyarısı vererek girişi engeller.
- **⚡ Bağlantıyı Kes & Sıfırla**: Yöneticinin tek tıkla kullanıcının oturum kilidini kaldırmasını veya bağlantısını sonlandırmasını sağlar.
- **📋 Panoya Kopyalama**: Üretilen giriş bilgilerini tek tıkla kopyalayıp misafire WhatsApp/SMS ile gönderebilirsiniz.

### 3. Esnek Çalışma Modları
- **🌐 Web Giriş Portalı Modu (Önerilen)**: Önce ortak Wi-Fi şifresiyle ağa katılınır, sonra giriş ekranı otomatik açılır; herkes kendi biletini girer (hesap başına 1 cihaz).
- **🔑 Standart Wi-Fi Modu**: Klasik ortak WPA2 şifresi ile doğrudan bağlanma, web arayüzü yok.

> Not: Windows 11 native hotspot API'si şifresiz (açık) yayına izin vermez — parola 8-63 karakter zorunludur ve kimlik doğrulama türlerinde `Open` seçeneği yoktur (yalnızca WPA2/WPA3). Bu yüzden portal modunda da ağa katılımda bir kez ortak şifre sorulur; telefon bunu hatırlar, sonraki bağlanmalarda direkt giriş ekranı açılır.

### 4. Windows 11 Masaüstü Entegrasyonu
- **Sistem Tepsisi (System Tray)**: Pencere kapatıldığında (X) yayın ve web portalı arka planda saatin yanında kesintisiz çalışmaya devam eder.
- **Canlı Donanım Takibi**: Wi-Fi donanımına bağlı tüm cihazlar, IP ve MAC adresleriyle canlı olarak listelenir.

### 5. İzleme, Kota, Hız Limiti & DNS Logu (📊 İzleme & Kota sekmesi)
- **Hotspot toplam hızı**: İndirme/yükleme Mbps, sanal adaptör sayaçlarından canlı ölçülür.
- **⏱ Süre kotası (hesap bazında)**: Kullanıcı seçip dakika verilir (0 = sınırsız). Süre dolunca oturum otomatik kapatılır, cihaz tekrar giriş ekranına düşer.
- **🚀 Hız limiti (örn. 10 Mbps, hesap + cihaz bazında)**: WinDivert sürücüsüyle istemci paketlerine token-bucket policing uygulanır (her yön için ayrı). Hesap limiti + cihaz limiti varsa en kısıtlayıcı olan geçerlidir.
- **📦 Veri kotası (örn. 5 GB, hesap + cihaz bazında)**: Up+down toplamı ölçülür, kota dolanın paketleri düşürülür ve hesap oturumu kapatılır; kotası dolan hesap tekrar giremez. Sayaçlar "Kullanımı Sıfırla" ile sıfırlanır, `users.json` içinde saklanır.
- **📡 Cihaz aktivitesi**: IP → MAC → kullanıcı eşleşmesi, DNS sorgu sayısı, veri kullanımı ve son aktivite zamanı.
- **🔍 DNS logu**: Hangi cihazın hangi siteye ne zaman girdiği ve isteğin iletilip iletilmediği; filtreleme, temizleme ve CSV dışa aktarma ile.

> Hız/veri kotası için `WinDivert.dll` + `WinDivert64.sys` exe'nin yanında olmalı (projede `Native/` altında, derlemede otomatik kopyalanır) ve program yönetici olarak çalışmalıdır. Sürücü pasifse panelde "Sürücü: ⚠ Yok" görünür; süre kotası ve DNS logu sürücüsüz de çalışır.

---

## 🚀 Nasıl Çalıştırılır?

Proje dizinindeki [`Başlat.bat`](file:///C:/Users/Ege/.gemini/antigravity/scratch/Win11HotspotManager/Başlat.bat) dosyasına çift tıklayarak uygulamayı anında açabilirsiniz.


---

## 🌐 Web Portalı Yerel Testi
Uygulama açıkken tarayıcınızdan `http://localhost:8080` veya Wi-Fi ağına bağlıyken `http://192.168.137.1:8080` adresine giderek giriş ekranını görebilirsiniz.
Hazır test hesapları:
- **Kullanıcı Adı**: `misafir_1` | **Şifre**: `Password123`
- **Kullanıcı Adı**: `misafir_2` | **Şifre**: `Password456`

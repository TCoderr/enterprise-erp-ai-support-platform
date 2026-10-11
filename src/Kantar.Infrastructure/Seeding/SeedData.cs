using Kantar.Domain.Identity;

namespace Kantar.Infrastructure.Seeding;

public sealed record SeedUser(string UserName, string DisplayName, string[] Roles, (string Warehouse, WarehouseLevel Level)[] Grants, decimal PoApprovalLimit = 0);

public sealed record SeedArticle(string Slug, string Visibility, string Module, string? Warehouse, string? ErrorCode, string Title, bool Publish, string Body);

/// <summary>Synthetic demo content. No real company, person, tax number or address appears here.</summary>
public static class SeedData
{
    public static readonly (string Code, string Name)[] Warehouses =
    [
        ("IST-01", "İstanbul Merkez"),
        ("ANK-01", "Ankara Bölge"),
        ("IZM-01", "İzmir Aktarma"),
    ];

    public static readonly (string Sku, string Name, string Unit, decimal Price, int Reorder)[] Products =
    [
        ("KBL-NYA-3X25", "NYA Kablo 3x2,5 mm² (100 m top)", "TOP", 4850.00m, 10),
        ("KBL-NYM-3X15", "NYM Kablo 3x1,5 mm² (100 m top)", "TOP", 3920.00m, 10),
        ("PRZ-TOP-BYZ", "Topraklı Priz Beyaz", "ADET", 68.50m, 150),
        ("SLT-TEK-BYZ", "Tekli Anahtar Beyaz", "ADET", 54.90m, 150),
        ("AMP-LED-9W", "LED Ampul 9 W E27 Gün Işığı", "ADET", 32.75m, 300),
        ("SGR-OTO-16A", "Otomatik Sigorta 16 A B Tipi", "ADET", 96.00m, 80),
        ("SGR-KAK-40A", "Kaçak Akım Rölesi 40 A 30 mA", "ADET", 845.00m, 20),
        ("VDA-SUN-4X40", "Sunta Vidası 4x40 (500'lü kutu)", "KOLI", 215.00m, 25),
        ("DBL-PLS-8", "Plastik Dübel 8 mm (100'lü paket)", "PAKET", 42.00m, 60),
        ("MTK-SDS-10", "SDS Plus Matkap Ucu 10x160", "ADET", 118.00m, 40),
        ("BNT-IZO-SYH", "İzole Bant Siyah 19 mm", "ADET", 21.50m, 200),
        ("KLP-KBL-25", "Kablo Kelepçesi 2,5x200 (100'lü)", "PAKET", 38.00m, 80),
        ("PNO-SVA-12", "Sıva Altı Sigorta Panosu 12'li", "ADET", 465.00m, 15),
        ("BRU-SPR-20", "Spiral Boru 20 mm (50 m top)", "TOP", 390.00m, 12),
    ];

    public static readonly (string Code, string Name, string TaxNumber, string Address)[] Customers =
    [
        ("MUS-YILDIZ", "Yıldız Yapı Malzemeleri (örnek)", "1000000011", "Örnek Sanayi Sitesi 4. Blok No: 12, Ümraniye / İstanbul"),
        ("MUS-MARMARA", "Marmara Elektrik Taahhüt (örnek)", "1000000029", "Deneme Caddesi No: 48, Kartal / İstanbul"),
        ("MUS-EGE", "Ege Hırdavat Pazarlama (örnek)", "1000000037", "Sentetik Mahallesi 1203. Sokak No: 7, Bornova / İzmir"),
        ("MUS-BASKENT", "Başkent İnşaat Tedarik (örnek)", "1000000045", "Kurgu Bulvarı No: 215, Yenimahalle / Ankara"),
    ];

    public static readonly (string Code, string Name)[] Suppliers =
    [
        ("TED-ANADOLU", "Anadolu Kablo Üretim (örnek)"),
        ("TED-TRAKYA", "Trakya Bağlantı Elemanları (örnek)"),
        ("TED-KUZEY", "Kuzey Aydınlatma Sanayi (örnek)"),
    ];

    public static readonly SeedUser[] Users =
    [
        new("satis1", "Satış Temsilcisi 1", [Roles.SalesAgent], [("IST-01", WarehouseLevel.View)]),
        new("satis2", "Satış Temsilcisi 2", [Roles.SalesAgent], [("IST-01", WarehouseLevel.View), ("ANK-01", WarehouseLevel.View)]),
        new("satis.mudur", "Satış Yöneticisi", [Roles.SalesManager], [("IST-01", WarehouseLevel.View), ("ANK-01", WarehouseLevel.View)]),
        new("satinalma1", "Satın Alma Uzmanı", [Roles.PurchasingAgent], [("IST-01", WarehouseLevel.View), ("ANK-01", WarehouseLevel.View)]),
        new("satinalma.onay", "Satın Alma Onaycısı", [Roles.PurchasingAgent], [("IST-01", WarehouseLevel.View), ("ANK-01", WarehouseLevel.View)], 250_000m),
        new("depo1", "Depo Görevlisi (İstanbul)", [Roles.WarehouseOperator], [("IST-01", WarehouseLevel.Operate)]),
        new("depo.sorumlu", "Depo Sorumlusu", [Roles.WarehouseOperator], [("IST-01", WarehouseLevel.Supervise), ("ANK-01", WarehouseLevel.Operate)]),
        new("depo.izmir", "Depo Görevlisi (İzmir)", [Roles.WarehouseOperator], [("IZM-01", WarehouseLevel.Operate)]),
        new("destek1", "Destek Uzmanı", [Roles.SupportAgent], []),
        new("destek.mudur", "Destek Yöneticisi", [Roles.SupportManager], []),
        new("admin", "Sistem Yöneticisi", [Roles.SystemAdmin], []),
    ];

    public static readonly Dictionary<string, (string Sku, int Quantity, decimal UnitCost)[]> OpeningStock = new(StringComparer.Ordinal)
    {
        ["IST-01"] =
        [
            ("KBL-NYA-3X25", 40, 3900.00m), ("KBL-NYM-3X15", 35, 3150.00m), ("PRZ-TOP-BYZ", 900, 48.00m), ("SLT-TEK-BYZ", 800, 37.50m),
            ("AMP-LED-9W", 1500, 21.00m), ("SGR-OTO-16A", 400, 66.00m), ("SGR-KAK-40A", 60, 610.00m), ("VDA-SUN-4X40", 120, 148.00m),
            ("DBL-PLS-8", 300, 27.00m), ("MTK-SDS-10", 110, 79.00m), ("BNT-IZO-SYH", 180, 13.20m), ("KLP-KBL-25", 240, 24.00m),
            ("PNO-SVA-12", 45, 330.00m), ("BRU-SPR-20", 30, 265.00m),
        ],
        ["ANK-01"] =
        [
            ("KBL-NYA-3X25", 15, 3900.00m), ("PRZ-TOP-BYZ", 300, 48.00m), ("SLT-TEK-BYZ", 260, 37.50m), ("AMP-LED-9W", 500, 21.00m),
            ("SGR-OTO-16A", 120, 66.00m), ("VDA-SUN-4X40", 40, 148.00m), ("DBL-PLS-8", 90, 27.00m),
        ],
        ["IZM-01"] =
        [
            ("AMP-LED-9W", 200, 21.00m), ("BNT-IZO-SYH", 150, 13.20m),
        ],
    };

    public static readonly SeedArticle[] Articles =
    [
        new("satis-siparisi-onaylama", "General", "Sales", null, null, "Satış siparişini onaylama ve stok ayırma", true,
            """
            ## Onay ne yapar
            Taslak siparişi onayladığınızda Kantar siparişin her satırı için depodaki kullanılabilir miktardan o satırın miktarı kadar stok ayırır. Ayırma ya bütün satırlar için yapılır ya da hiç yapılmaz: tek bir satırda stok yetmezse sipariş taslak olarak kalır ve hiçbir satır için stok ayrılmaz.

            ## Adımlar
            1. Satış > Siparişler ekranından taslak siparişi açın.
            2. Satır miktarlarını ve müşteriyi kontrol edin. Fiyatlar ürün kartından sunucu tarafından alınır; elle fiyat girilmez.
            3. Onayla düğmesine basın. Onaydan sonra satır eklenemez ve miktar değiştirilemez.

            ## Onaydan sonra
            Sipariş durumu "Onaylandı" olur ve depo görevlisinin "Sevk bekleyenler" listesinde görünür. Ayrılan miktar stok ekranında "Rezerve" sütununda yer alır; "Kullanılabilir" miktar aynı kadar azalır. Eldeki miktar yalnızca sevkiyatla değişir.

            ## Onay reddedilirse
            STK-409 hatası yeterli kullanılabilir stok olmadığını gösterir; hata iletisinde hangi satırın yetmediği ve kullanılabilir miktar yazar. SIP-412 hatası siparişin siz görüntüledikten sonra başkası tarafından değiştirildiğini gösterir; sayfayı yenileyip yeniden deneyin.
            """),
        new("hata-stk-409", "General", "Inventory", null, "STK-409", "STK-409: Yeterli kullanılabilir stok yok", true,
            """
            ## Anlamı
            STK-409, istenen miktarın depodaki kullanılabilir miktardan fazla olduğunu bildirir. Kullanılabilir miktar, eldeki miktardan başka siparişler için ayrılmış (rezerve) miktarın çıkarılmasıyla bulunur. Stok listesinde ürün "var" görünse bile tamamı başka siparişlere ayrılmış olabilir.

            ## Ne zaman görülür
            Satış siparişi onaylanırken, depolar arası transferde ve eksi yönlü stok düzeltmesinde görülür. İşlem bütünüyle reddedilir; kısmen uygulanmaz.

            ## Ne yapılır
            1. Depo > Stok bakiyeleri ekranında ürünün Eldeki, Rezerve ve Kullanılabilir değerlerine bakın.
            2. Sipariş miktarını kullanılabilir miktara indirin ya da kalan miktar için ayrı bir sipariş açın.
            3. Beklenen bir mal kabul varsa satın alma ile görüşün; mal kabul yapıldığında kullanılabilir miktar artar.
            4. Rezerve miktarın yanlış olduğunu düşünüyorsanız destek talebi açın. Rezervasyonlar elle silinmez.
            """),
        new("hata-sip-412", "General", "General", null, "SIP-412", "SIP-412: Belge siz görüntüledikten sonra değişti", true,
            """
            ## Anlamı
            SIP-412, kaydetmeye ya da onaylamaya çalıştığınız belgenin siz açtıktan sonra başka bir kullanıcı tarafından değiştirildiğini gösterir. Kantar, başkasının değişikliğinin üzerine sessizce yazmaz.

            ## Ne yapılır
            1. Sayfayı yenileyin ve belgenin güncel halini inceleyin.
            2. Değişikliğiniz hâlâ gerekliyse yeniden yapın ve kaydedin.

            Yaptığınız değişiklikler bu durumda kaydedilmemiştir. Aynı işlemi yenilemeden tekrar göndermek sonucu değiştirmez.
            """),
        new("mal-kabul-kismi-teslimat", "General", "Inventory", null, "MKB-409", "Mal kabul ve kısmi teslimat", true,
            """
            ## Mal kabul nasıl yapılır
            1. Depo > Kabul bekleyenler listesinden onaylı satın alma siparişini açın.
            2. Tedarikçi irsaliye numarasını girin. Aynı tedarikçinin aynı irsaliye numarası ikinci kez kabul edilemez.
            3. Gelen miktarları satır satır yazın ve Mal kabulü kaydet düğmesine basın.

            ## Kısmi teslimat
            Siparişin tamamı gelmediyse yalnızca gelen miktarı girin. Sipariş "Kısmen kabul edildi" durumuna geçer ve kalan miktar bir sonraki teslimat için açık kalır.

            ## MKB-409 hatası
            Girilen miktar satırın kalan miktarından fazlaysa MKB-409 hatası alınır ve hiçbir satır kaydedilmez. Fazla gelen ürün için satın alma uzmanından yeni bir sipariş açmasını isteyin; fazla kabul yapılamaz.

            ## Yanlış kabul
            Kaydedilmiş mal kabul değiştirilemez ve silinemez. Sayım farkı varsa depo sorumlusu gerekçeli bir stok düzeltmesi girer.
            """),
        new("depolar-arasi-transfer", "General", "Inventory", null, null, "Depolar arası transfer", true,
            """
            ## Kimler yapabilir
            Transferi, hem kaynak hem hedef depoda işlem yetkisi olan depo görevlisi yapar. Yalnızca bir depoda yetkiniz varsa transfer başlatamazsınız.

            ## Adımlar
            1. Depo > Transfer ekranında kaynak ve hedef depoyu seçin.
            2. Transfer referansını yazın. Her referans yalnızca bir kez kullanılabilir.
            3. Ürünleri ve miktarları ekleyin ve Transferi kaydet düğmesine basın.

            ## Dikkat edilecekler
            Yalnızca kullanılabilir stok taşınabilir; başka siparişlere ayrılmış miktar transfer edilemez. Transfer tek adımda tamamlanır: ürün kaynak depodan düşer ve aynı anda hedef depoya girer. Yolda bekleyen stok durumu yoktur.
            """),
        new("ist-01-sevk-rampasi", "General", "Inventory", "IST-01", null, "İstanbul Merkez: sevk rampası çalışma düzeni", true,
            """
            ## Rampa saatleri
            İstanbul Merkez deposunda sevkiyat rampası hafta içi 08.30 ile 17.30 arasında çalışır. Saat 16.00'dan sonra onaylanan siparişler ertesi iş günü sevk edilir.

            ## Sevk referansı
            Bu depoda sevk referansı olarak irsaliye koçanındaki seri ve sıra numarası birlikte yazılır. Aynı sipariş için aynı referans ikinci kez kullanılamaz.

            ## Kısmi sevk
            Araç kapasitesi yetmediğinde kısmi sevk yapılabilir. Kalan miktar siparişte açık kalır ve rezervasyonu korunur.
            """),
        new("destek-stok-farki-incelemesi", "SupportOnly", "Support", null, null, "Destek personeli: stok farkı şikâyetlerinde inceleme adımları", true,
            """
            ## Kapsam
            Bu belge yalnızca destek personeli içindir.

            ## Adımlar
            1. Talepteki depo ve ürün için Depo > Hareketler listesini isteyin; son hareketin "sonraki eldeki" değeri ile bakiyeyi karşılaştırın.
            2. Yönetim > Operasyon ekranındaki son mutabakat çalışmasının sonucuna bakılmasını sistem yöneticisinden isteyin. Mutabakat yalnızca rapor üretir; hiçbir bakiyeyi düzeltmez.
            3. Fiziksel sayım sistemdeki miktardan farklıysa çözüm, depo sorumlusunun gerekçeli stok düzeltmesi girmesidir. Destek personeli stok düzeltmesi yapmaz.
            4. Mutabakat raporu hareket günlüğü ile bakiye arasında fark gösteriyorsa bu uygulama içinden düzeltilmez; talebi sistem yöneticisine yönlendirin.

            ## Talebe yazılmayacaklar
            Talep yorumlarına müşteri vergi numarası, parola veya ekran görüntüsündeki kişisel bilgiler yazılmaz.
            """),
        new("yonetici-kalan-iptal-kararlari", "ManagerOnly", "Sales", null, null, "Yönetici notu: kalan miktarı iptal etme kararları", true,
            """
            ## Kapsam
            Bu belge yalnızca yöneticiler içindir.

            ## Karar ölçütleri
            Kısmen sevk edilmiş bir siparişin kalanını iptal etmeden önce müşteriyle yazılı teyit alınır. İptal, yalnızca sevk edilmemiş miktarın rezervasyonunu serbest bırakır; sevk edilen ürün stoğa geri dönmüş sayılmaz.

            ## Kayıt
            İptal gerekçesi alanına teyidin tarihi ve kanalı yazılır. İade süreci bu uygulamanın kapsamında değildir.
            """),
        new("hata-ref-409", "General", "General", null, "REF-409", "REF-409: Bu referans daha önce kullanıldı", true,
            """
            ## Anlamı
            REF-409, girdiğiniz irsaliye, sevk, transfer ya da düzeltme referansının daha önce kaydedildiğini gösterir. İşlem ikinci kez uygulanmamıştır; stok bir kez değişmiştir.

            ## Ne yapılır
            1. İlgili belgenin geçmişinde aynı referansla yapılmış kaydı bulun.
            2. Gerçekten yeni bir teslimat ya da sevkiyat söz konusuysa kendi referansını kullanın.

            Bağlantı koptuğu için sonucu göremediyseniz aynı işlemi yeniden göndermeniz güvenlidir: Kantar ilk sonucu geri verir ya da bu hatayı döndürür.
            """),
        new("satin-alma-onay-limiti", "General", "Purchasing", null, "YTK-403", "Satın alma siparişi onay limiti", true,
            """
            ## Kural
            Her satın alma kullanıcısının bir onay limiti vardır. Sipariş toplamı limitin üzerindeyse onay reddedilir ve YTK-403 hatası gösterilir. Limiti sıfır olan kullanıcı sipariş hazırlayabilir ama onaylayamaz.

            ## Ne yapılır
            Siparişi, limiti yeterli olan bir satın alma onaycısına yönlendirin. Limit değişikliği sistem yöneticisi tarafından yapılır ve denetim kaydına yazılır.
            """),
        new("destek-talebi-ve-kaynakli-yardim", "General", "Support", null, null, "Destek talebi açma ve Kaynaklı Yardım", true,
            """
            ## Destek talebi
            Destek > Yeni talep ekranından başlık, açıklama, modül ve varsa hata kodunu girin. İlgili belgenin türünü ve numarasını yazabilirsiniz; bu bilgi yalnızca etiket olarak saklanır ve destek personeline belgeyi görme yetkisi vermez.

            ## Kaynaklı Yardım
            Kaynaklı Yardım paneli, sorunuzu yalnızca okuma yetkiniz olan yayınlı bilgi bankası belgelerine dayanarak yanıtlar ve hangi belgenin hangi bölümüne dayandığını gösterir. Yeterli kaynak yoksa bunu açıkça söyler.

            ## Öneriyi talebe kaydetme
            Yanıtı uygun bulursanız "Talebe öneri olarak kaydet" düğmesiyle kendi talebinize not olarak ekleyebilirsiniz. Bu adımı siz başlatırsınız; yardım paneli kendiliğinden talebe yazmaz, talebi kapatmaz ve hiçbir ERP kaydını değiştirmez.
            """),
        new("taslak-iade-sureci", "General", "Sales", null, null, "Taslak: iade süreci (yayınlanmadı)", false,
            """
            ## Durum
            Bu belge taslaktır ve yayınlanmamıştır. İade süreci ürün kapsamında değildir; bu metin yalnızca taslak belgelerin okuyuculara ve yardım yanıtlarına hiç yansımadığını göstermek için vardır.
            """),
    ];
}

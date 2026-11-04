using System.Text.Json;
using MAIC.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public static class DynamicRbacSeeder
{
    public static readonly string[] AllPermissions =
    [
        "inbox.view",
        "inbox.reply",
        "knowledge.manage",
        "channels.manage",
        "team.manage",
        "billing.manage",
        "settings.manage",
        "widget.manage"
    ];

    public static async Task EnsureSeededAsync(MaicDbContext db, CancellationToken cancellationToken = default)
    {
        var changed = false;

        if (!await db.WorkspaceRolePermissions.AnyAsync(cancellationToken))
        {
            foreach (var permission in AllPermissions)
            {
                db.WorkspaceRolePermissions.Add(new WorkspaceRolePermission(WorkspaceRole.OWNER, permission, isGranted: true));
                db.WorkspaceRolePermissions.Add(new WorkspaceRolePermission(
                    WorkspaceRole.ADMIN,
                    permission,
                    isGranted: permission != "billing.manage"));
                db.WorkspaceRolePermissions.Add(new WorkspaceRolePermission(
                    WorkspaceRole.AGENT,
                    permission,
                    isGranted: permission is "inbox.view" or "inbox.reply"));
            }
            changed = true;
        }

        if (!await db.PlanDefinitions.AnyAsync(cancellationToken))
        {
            db.PlanDefinitions.AddRange(
                GetDefaultPlanDefinition(PlanTier.FREE),
                GetDefaultPlanDefinition(PlanTier.STARTER),
                GetDefaultPlanDefinition(PlanTier.PRO),
                GetDefaultPlanDefinition(PlanTier.SCALE)
            );
            changed = true;
        }
        else
        {
            var existingPlans = await db.PlanDefinitions.ToListAsync(cancellationToken);
            foreach (var existing in existingPlans)
            {
                if (existing.Plan != PlanTier.FREE && existing.MonthlyPriceTry == 0m)
                {
                    var defaults = GetDefaultPlanDefinition(existing.Plan);
                    existing.Update(
                        existing.Name,
                        existing.MonthlyMessageLimit,
                        existing.StorageLimitBytes,
                        existing.MaxSeats,
                        existing.AllowedFeaturesJson,
                        existing.MaxFileSizeBytes,
                        existing.MaxDocumentCount,
                        existing.MaxUrlSourceCount,
                        existing.PlaygroundMonthlyLimit,
                        defaults.MonthlyPriceTry);
                    changed = true;
                }
            }
        }

        if (!await db.WizardCatalogItems.AnyAsync(cancellationToken))
        {
            db.WizardCatalogItems.AddRange(GetDefaultWizardCatalogItems());
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public static IReadOnlyList<WizardCatalogItem> GetDefaultWizardCatalogItems() =>
    [
        // USE_CASE
        new WizardCatalogItem(
            category: "USE_CASE",
            key: "ecommerce",
            title: "Satış / E-ticaret",
            description: "Ürün, sipariş öncesi soru, teslimat ve dönüşüm odaklı akışlar.",
            iconName: "ShoppingBag",
            sortOrder: 1,
            isActive: true,
            defaultWelcomeMessage: "Merhaba! Ürünlerimiz, kargo süreçlerimiz veya siparişiniz hakkında size nasıl yardımcı olabilirim?",
            defaultBusinessSummary: "Online mağazamız üzerinden müşterilerimize hızlı kargo, güvenli ödeme ve kolay iade garantisiyle ürünler sunuyoruz.",
            defaultKeyFaq: "Kargo kaç günde gelir? -> Siparişleriniz 1-3 iş günü içinde kargoya verilir.\nİade süresi kaç gündür? -> Teslimattan itibaren 14 gün içinde ücretsiz iade edebilirsiniz.",
            systemPromptHint: "E-ticaret satış ve destek uzmanısın. Ürün özelliklerini, teslimat/iade koşullarını net açıkla ve satın alma dönüşümünü kolaylaştır."),
        new WizardCatalogItem(
            category: "USE_CASE",
            key: "education",
            title: "Eğitim / Üniversite",
            description: "Program, bölüm, başvuru ve öğrenci bilgi akışları.",
            iconName: "GraduationCap",
            sortOrder: 2,
            isActive: true,
            defaultWelcomeMessage: "Merhaba! Eğitim programlarımız, başvuru koşulları ve kayıt süreçleri hakkında sorularınızı yanıtlayabilirim.",
            defaultBusinessSummary: "Kurumumuz öğrencilere ve adaylara akademik programlar, sertifika eğitimleri ve kariyer gelişim olanakları sunar.",
            defaultKeyFaq: "Başvurular ne zaman başlıyor? -> Akademik takvimimize göre başvurularımız online olarak alınmaktadır.\nBurs imkanları var mı? -> Başarı ve ihtiyaç burslarımız mevcuttur.",
            systemPromptHint: "Eğitim kurumu danışmanısın. Aday öğrencilere ve velilere programlar, kayıt şartları ve takvim hakkında yol gösterici ve güven verici yanıtlar üret."),
        new WizardCatalogItem(
            category: "USE_CASE",
            key: "health_tourism",
            title: "Sağlık Turizmi",
            description: "Ön bilgilendirme, lead toplama ve güvenli insan devri.",
            iconName: "Stethoscope",
            sortOrder: 3,
            isActive: true,
            defaultWelcomeMessage: "Merhaba, sağlıklı günler diliyorum. Tedavi süreçlerimiz ve ön değerlendirme randevusu hakkında size nasıl yardımcı olabilirim?",
            defaultBusinessSummary: "Kliniğimiz uluslararası ve yerel hastalara uzman hekim kadrosuyla ön bilgilendirme, tedavi planlaması ve konaklama/transfer koordinasyonu sunar.",
            defaultKeyFaq: "Ücretsiz ön görüşme var mı? -> Evet, uzman ekibimizle online ön değerlendirme planlayabiliriz.\nSüreç nasıl işliyor? -> Ön değerlendirme sonrası kişiye özel tedavi ve seyahat planı hazırlanır.",
            systemPromptHint: "Sağlık turizmi bilgi ve koordinasyon asistanısın. Asla kesin tıbbi teşhis koyma; süreç hakkında ön bilgilendirme yap, hastanın ihtiyacını anla ve medikal danışmana güvenli devir sağla."),
        new WizardCatalogItem(
            category: "USE_CASE",
            key: "software",
            title: "Yazılım / Hizmet",
            description: "Ürün anlatımı, onboarding ve destek süreçleri.",
            iconName: "Cpu",
            sortOrder: 4,
            isActive: true,
            defaultWelcomeMessage: "Merhaba! Ürün özelliklerimiz, paketlerimiz veya teknik kurulum hakkında size nasıl yardımcı olabilirim?",
            defaultBusinessSummary: "SaaS platformumuz işletmelerin operasyonel süreçlerini hızlandıran bulut tabanlı yazılım ve otomasyon çözümleri sunar.",
            defaultKeyFaq: "Ücretsiz deneme var mı? -> Evet, kredi kartı gerekmeden 14 gün ücretsiz deneyebilirsiniz.\nKurulum ne kadar sürer? -> Dakikalar içinde hesabınızı oluşturup kullanmaya başlayabilirsiniz.",
            systemPromptHint: "Yazılım (SaaS) ürün uzmanısın. Özellikleri, entegrasyon adımlarını ve paket farklarını adım adım, anlaşılır bir dille açıkla."),
        new WizardCatalogItem(
            category: "USE_CASE",
            key: "corporate",
            title: "Genel Kurum / İşletme",
            description: "Belirli bir dikeye bağlı olmayan bilgi ve yönlendirme akışları.",
            iconName: "Building2",
            sortOrder: 5,
            isActive: true,
            defaultWelcomeMessage: "Merhaba! Kurumumuz ve hizmetlerimiz hakkında size nasıl yardımcı olabilirim?",
            defaultBusinessSummary: "Kurumumuz müşterilerine profesyonel hizmet, hızlı bilgi akışı ve çözüm odaklı destek sunar.",
            defaultKeyFaq: "Çalışma saatleriniz nedir? -> Hafta içi 09:00 - 18:00 saatleri arasında hizmet veriyoruz.\nSize nasıl ulaşabilirim? -> Bu sohbet ekranından veya iletişim kanallarımızdan bize ulaşabilirsiniz.",
            systemPromptHint: "Kurumsal iletişim asistanısın. Ziyaretçileri kurumsal, nazik ve doğru bilgiyle karşıla; gerektiğinde ilgili departmana yönlendir."),

        // ASSISTANT_ROLE
        new WizardCatalogItem(
            category: "ASSISTANT_ROLE",
            key: "pre_sales",
            title: "Satış öncesi soruları cevaplasın",
            description: "Fiyat, ürün, uygunluk, teslimat ve dönüşüm sorularını karşılasın.",
            iconName: "Sparkles",
            sortOrder: 1,
            isActive: true,
            systemPromptHint: "Öncelikli rolün satış öncesi (pre-sales) danışmanlıktır. Ziyaretçinin kararsızlıklarını gider, ürün/hizmet avantajlarını vurgula ve satın alma/başvuru adımına teşvik et."),
        new WizardCatalogItem(
            category: "ASSISTANT_ROLE",
            key: "info_desk",
            title: "Bilgi masası gibi çalışsın",
            description: "Kurumun ne yaptığını, nasıl ilerlenmesi gerektiğini anlatsın.",
            iconName: "MessageSquareText",
            sortOrder: 2,
            isActive: true,
            systemPromptHint: "Öncelikli rolün bilgi masası (info desk) uzmanlığıdır. Kurumun süreçlerini, kurallarını ve sıkça sorulan soruları açık, net ve tarafsız şekilde yanıtla."),
        new WizardCatalogItem(
            category: "ASSISTANT_ROLE",
            key: "lead_capture",
            title: "Lead toplasın ve filtrelesin",
            description: "İlk ihtiyacı anlasın, bilgileri toparlasın ve doğru kişiye aktarsın.",
            iconName: "Filter",
            sortOrder: 3,
            isActive: true,
            systemPromptHint: "Öncelikli rolün aday müşteri kazanımı (lead capture) ve filtrelemedir. Ziyaretçinin temel ihtiyacını öğren, iletişim bilgilerini veya talebini netleştir ve uzman temsilciye devret."),

        // PRIMARY_CHANNEL
        new WizardCatalogItem(
            category: "PRIMARY_CHANNEL",
            key: "website",
            title: "Web sitesi",
            description: "Ziyaretçilerin ilk temas noktası siten.",
            iconName: "Globe",
            sortOrder: 1,
            isActive: true),
        new WizardCatalogItem(
            category: "PRIMARY_CHANNEL",
            key: "instagram",
            title: "Instagram / DM",
            description: "Sorular çoğunlukla sosyal medyadan geliyor.",
            iconName: "Instagram",
            sortOrder: 2,
            isActive: true),
        new WizardCatalogItem(
            category: "PRIMARY_CHANNEL",
            key: "whatsapp",
            title: "WhatsApp",
            description: "Hızlı ve direkt mesaj akışı ağırlıklı.",
            iconName: "MessageCircle",
            sortOrder: 3,
            isActive: true),
        new WizardCatalogItem(
            category: "PRIMARY_CHANNEL",
            key: "marketplace",
            title: "Pazar yeri",
            description: "Trendyol benzeri mağaza akışları öncelikli.",
            iconName: "Store",
            sortOrder: 4,
            isActive: true),
        new WizardCatalogItem(
            category: "PRIMARY_CHANNEL",
            key: "omnichannel",
            title: "Birden fazla kanal",
            description: "Soru kaynağı tek yer değil, AI ortak yüz olsun.",
            iconName: "Layers",
            sortOrder: 5,
            isActive: true),
        new WizardCatalogItem(
            category: "PRIMARY_CHANNEL",
            key: "other",
            title: "Diğer",
            description: "Özel veya hibrit bir iletişim akışın var.",
            iconName: "Edit3",
            sortOrder: 6,
            isActive: true),

        // KNOWLEDGE_METHOD
        new WizardCatalogItem(
            category: "KNOWLEDGE_METHOD",
            key: "form",
            title: "Ben kısa kısa anlatacağım",
            description: "Hızlı başlamak istiyorum, bilgiyi formdan vereceğim.",
            iconName: "FileEdit",
            sortOrder: 1,
            isActive: true),
        new WizardCatalogItem(
            category: "KNOWLEDGE_METHOD",
            key: "website",
            title: "Web sitemden alacağım",
            description: "Sonra sayfa taraması veya URL ile güçlendireceğim.",
            iconName: "Globe",
            sortOrder: 2,
            isActive: true),
        new WizardCatalogItem(
            category: "KNOWLEDGE_METHOD",
            key: "files",
            title: "Dosya ve doküman yükleyeceğim",
            description: "PDF, katalog, fiyat listesi veya iç doküman kullanacağım.",
            iconName: "FileText",
            sortOrder: 3,
            isActive: true)
    ];

    public static PlanDefinition GetDefaultPlanDefinition(PlanTier plan) => plan switch
    {
        PlanTier.FREE => new PlanDefinition(
            PlanTier.FREE,
            "Free",
            monthlyMessageLimit: 100,
            storageLimitBytes: 10L * 1024 * 1024,
            maxSeats: 1,
            allowedFeaturesJson: JsonSerializer.Serialize(new[] { "web_widget", "basic_ai", "knowledge_text" }),
            maxFileSizeBytes: 2L * 1024 * 1024,
            maxDocumentCount: 3,
            maxUrlSourceCount: 0,
            playgroundMonthlyLimit: 200,
            monthlyPriceTry: 0m),
        PlanTier.STARTER => new PlanDefinition(
            PlanTier.STARTER,
            "Starter",
            monthlyMessageLimit: 5000,
            storageLimitBytes: 100L * 1024 * 1024,
            maxSeats: 3,
            allowedFeaturesJson: JsonSerializer.Serialize(new[] { "web_widget", "direct_share", "knowledge_file", "knowledge_text", "knowledge_website", "ai_rules", "team_basic" }),
            maxFileSizeBytes: 10L * 1024 * 1024,
            maxDocumentCount: 25,
            maxUrlSourceCount: 1,
            playgroundMonthlyLimit: 200,
            monthlyPriceTry: 490m),
        PlanTier.PRO => new PlanDefinition(
            PlanTier.PRO,
            "Pro",
            monthlyMessageLimit: 25000,
            storageLimitBytes: 500L * 1024 * 1024,
            maxSeats: 10,
            allowedFeaturesJson: JsonSerializer.Serialize(new[] { "web_widget", "direct_share", "custom_branding", "remove_badge", "knowledge_file", "knowledge_text", "knowledge_website", "ai_rules", "team_advanced", "analytics" }),
            maxFileSizeBytes: 25L * 1024 * 1024,
            maxDocumentCount: 150,
            maxUrlSourceCount: 10,
            playgroundMonthlyLimit: 200,
            monthlyPriceTry: 1490m),
        PlanTier.SCALE => new PlanDefinition(
            PlanTier.SCALE,
            "Scale",
            monthlyMessageLimit: 100000,
            storageLimitBytes: 2L * 1024 * 1024 * 1024,
            maxSeats: 50,
            allowedFeaturesJson: JsonSerializer.Serialize(new[] { "web_widget", "direct_share", "custom_branding", "remove_badge", "knowledge_file", "knowledge_text", "knowledge_website", "ai_rules", "team_advanced", "analytics", "priority_support", "custom_limits" }),
            maxFileSizeBytes: 50L * 1024 * 1024,
            maxDocumentCount: 1000,
            maxUrlSourceCount: 50,
            playgroundMonthlyLimit: 200,
            monthlyPriceTry: 4990m),
        _ => throw new ArgumentOutOfRangeException(nameof(plan))
    };

    public static IReadOnlyList<string> GetDefaultPermissions(WorkspaceRole role) => role switch
    {
        WorkspaceRole.OWNER => AllPermissions,
        WorkspaceRole.ADMIN => AllPermissions.Where(p => p != "billing.manage").ToArray(),
        WorkspaceRole.AGENT => ["inbox.view", "inbox.reply"],
        _ => []
    };

    public static IReadOnlyList<string> GetDefaultPlanFeatures(PlanTier plan) => plan switch
    {
        PlanTier.FREE => ["web_widget", "basic_ai", "knowledge_text"],
        PlanTier.STARTER => ["web_widget", "direct_share", "knowledge_file", "knowledge_text", "knowledge_website", "ai_rules", "team_basic"],
        PlanTier.PRO => ["web_widget", "direct_share", "custom_branding", "remove_badge", "knowledge_file", "knowledge_text", "knowledge_website", "ai_rules", "team_advanced", "analytics"],
        PlanTier.SCALE => ["web_widget", "direct_share", "custom_branding", "remove_badge", "knowledge_file", "knowledge_text", "knowledge_website", "ai_rules", "team_advanced", "analytics", "priority_support", "custom_limits"],
        _ => []
    };
}

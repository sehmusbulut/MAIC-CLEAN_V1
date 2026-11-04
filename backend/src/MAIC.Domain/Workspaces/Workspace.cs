namespace MAIC.Domain.Workspaces;

public enum PlanTier
{
    FREE = 0,
    STARTER = 1,
    PRO = 2,
    SCALE = 3
}

public sealed class Workspace
{
    private Workspace() { }
    public Workspace(string name) => Rename(name);

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public PlanTier Plan { get; private set; } = PlanTier.STARTER;
    public int MonthlyMessageCap { get; private set; } = 5000;
    public int UsedMessagesThisMonth { get; private set; } = 0;
    public long StorageLimitBytes { get; private set; } = 104_857_600;
    public long UsedStorageBytes { get; private set; } = 0;

    public int PlaygroundMonthlyCap { get; private set; } = 200;
    public int UsedPlaygroundMessagesThisMonth { get; private set; } = 0;

    // Step 1: Branding
    public string? BrandName { get; private set; }
    public string WidgetTitle { get; private set; } = "Canlı Destek";
    public string WidgetSubtitle { get; private set; } = "Genel destek • Online";
    public string PrimaryColor { get; private set; } = "#7c3aed";
    public string? LogoUrl { get; private set; }
    public bool ShowBadge { get; private set; } = true;
    public bool DirectShareActive { get; private set; } = true;
    public string DirectShareKey { get; private set; } = Guid.NewGuid().ToString("D");

    // Step 1: Configuration Choices
    public string UseCase { get; private set; } = "ecommerce";
    public string AssistantRole { get; private set; } = "info_desk";
    public string PrimaryChannel { get; private set; } = "website";
    public string InitialKnowledgeMethod { get; private set; } = "form";

    // Step 2-4: Content, Automation, Notifications & Security
    public string WelcomeMessage { get; private set; } = "Merhaba, sağlıklı günler diliyorum.";
    public string? BusinessSummary { get; private set; }
    public string? KeyFaq { get; private set; }
    public string? AnswerBoundaries { get; private set; }
    public string? EscalationRules { get; private set; }
    public string AiScheduleMode { get; private set; } = "ALWAYS_AI";
    public bool SoundNotificationEnabled { get; private set; } = true;
    public string SoundTone { get; private set; } = "classic";
    public string? AllowedDomains { get; private set; }

    public static bool IsValidName(string? name) => !string.IsNullOrWhiteSpace(name)
        && name.Trim().Length <= 200 && !name.Any(char.IsControl);

    public void Rename(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("Name must contain 1–200 characters without control characters.", nameof(name));
        Name = name.Trim();
    }

    public void UpdateBranding(
        string widgetTitle,
        string? brandName,
        string primaryColor,
        string? logoUrl,
        bool showBadge,
        bool directShareActive = true,
        string? widgetSubtitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryColor);

        WidgetTitle = widgetTitle.Trim();
        if (!string.IsNullOrWhiteSpace(widgetSubtitle))
        {
            WidgetSubtitle = widgetSubtitle.Trim();
        }
        BrandName = string.IsNullOrWhiteSpace(brandName) ? null : brandName.Trim();
        PrimaryColor = primaryColor.Trim();
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
        ShowBadge = showBadge;
        DirectShareActive = directShareActive;
    }

    public void SetLogoUrl(string? logoUrl)
    {
        LogoUrl = string.IsNullOrWhiteSpace(logoUrl) ? null : logoUrl.Trim();
    }

    public void UpdateWizardSettings(
        string? brandName,
        string? widgetTitle,
        string? widgetSubtitle,
        string? primaryColor,
        bool showBadge,
        bool directShareActive,
        string? useCase,
        string? assistantRole,
        string? primaryChannel,
        string? initialKnowledgeMethod,
        string? welcomeMessage,
        string? businessSummary,
        string? keyFaq,
        string? answerBoundaries,
        string? escalationRules,
        string? aiScheduleMode,
        bool soundNotificationEnabled,
        string? soundTone,
        string? allowedDomains)
    {
        BrandName = string.IsNullOrWhiteSpace(brandName) ? null : brandName.Trim();
        if (!string.IsNullOrWhiteSpace(widgetTitle)) WidgetTitle = widgetTitle.Trim();
        if (!string.IsNullOrWhiteSpace(widgetSubtitle)) WidgetSubtitle = widgetSubtitle.Trim();
        if (!string.IsNullOrWhiteSpace(primaryColor)) PrimaryColor = primaryColor.Trim();
        ShowBadge = showBadge;
        DirectShareActive = directShareActive;

        if (!string.IsNullOrWhiteSpace(useCase)) UseCase = useCase.Trim();
        if (!string.IsNullOrWhiteSpace(assistantRole)) AssistantRole = assistantRole.Trim();
        if (!string.IsNullOrWhiteSpace(primaryChannel)) PrimaryChannel = primaryChannel.Trim();
        if (!string.IsNullOrWhiteSpace(initialKnowledgeMethod)) InitialKnowledgeMethod = initialKnowledgeMethod.Trim();

        if (!string.IsNullOrWhiteSpace(welcomeMessage)) WelcomeMessage = welcomeMessage.Trim();
        UpdateAiRules(businessSummary, keyFaq, answerBoundaries, escalationRules);

        if (!string.IsNullOrWhiteSpace(aiScheduleMode)) AiScheduleMode = aiScheduleMode.Trim();
        SoundNotificationEnabled = soundNotificationEnabled;
        if (!string.IsNullOrWhiteSpace(soundTone)) SoundTone = soundTone.Trim();
        AllowedDomains = string.IsNullOrWhiteSpace(allowedDomains) ? null : allowedDomains.Trim();
    }

    public void UpdateAiRules(
        string? businessSummary,
        string? keyFaq,
        string? answerBoundaries,
        string? escalationRules)
    {
        BusinessSummary = string.IsNullOrWhiteSpace(businessSummary) ? null : businessSummary.Trim();
        KeyFaq = string.IsNullOrWhiteSpace(keyFaq) ? null : keyFaq.Trim();
        AnswerBoundaries = string.IsNullOrWhiteSpace(answerBoundaries) ? null : answerBoundaries.Trim();
        EscalationRules = string.IsNullOrWhiteSpace(escalationRules) ? null : escalationRules.Trim();
    }

    public void UpdatePlan(PlanTier plan, int monthlyMessageCap, long storageLimitBytes, int playgroundMonthlyCap = 200)
    {
        if (!Enum.IsDefined(plan)) throw new ArgumentOutOfRangeException(nameof(plan));
        if (monthlyMessageCap < 0) throw new ArgumentOutOfRangeException(nameof(monthlyMessageCap));
        if (storageLimitBytes < 0) throw new ArgumentOutOfRangeException(nameof(storageLimitBytes));
        if (playgroundMonthlyCap < 0) throw new ArgumentOutOfRangeException(nameof(playgroundMonthlyCap));

        Plan = plan;
        MonthlyMessageCap = monthlyMessageCap;
        StorageLimitBytes = storageLimitBytes;
        PlaygroundMonthlyCap = playgroundMonthlyCap;
    }

    public void SetUsedStorageBytes(long bytes) => UsedStorageBytes = Math.Max(0, bytes);

    public void AddStorageUsage(long deltaBytes)
    {
        if (deltaBytes < 0) throw new ArgumentOutOfRangeException(nameof(deltaBytes));
        UsedStorageBytes += deltaBytes;
    }

    public void ReduceStorageUsage(long deltaBytes)
    {
        if (deltaBytes < 0) throw new ArgumentOutOfRangeException(nameof(deltaBytes));
        UsedStorageBytes = Math.Max(0, UsedStorageBytes - deltaBytes);
    }

    public bool CanAcceptStorage(long additionalBytes, long? effectiveLimitBytes = null) =>
        UsedStorageBytes + Math.Max(0, additionalBytes) <= (effectiveLimitBytes ?? StorageLimitBytes);

    public void IncrementMessageUsage(int count = 1)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        UsedMessagesThisMonth += count;
    }

    public void IncrementPlaygroundUsage(int count = 1)
    {
        if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
        UsedPlaygroundMessagesThisMonth += count;
    }

    public void ResetMonthlyUsage()
    {
        UsedMessagesThisMonth = 0;
        UsedPlaygroundMessagesThisMonth = 0;
    }

    public void UpdateWidgetConfig(bool directShareActive, string? allowedDomains)
    {
        DirectShareActive = directShareActive;
        AllowedDomains = string.IsNullOrWhiteSpace(allowedDomains) ? null : allowedDomains.Trim();
    }

    public string RegenerateDirectShareKey()
    {
        DirectShareKey = Guid.NewGuid().ToString("D");
        return DirectShareKey;
    }
}

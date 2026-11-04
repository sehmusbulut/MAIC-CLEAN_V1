namespace MAIC.Domain.Workspaces;

public sealed class WorkspaceRolePermission
{
    private WorkspaceRolePermission() { }

    public WorkspaceRolePermission(WorkspaceRole role, string permissionKey, bool isGranted)
    {
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionKey);

        Role = role;
        PermissionKey = permissionKey.Trim();
        IsGranted = isGranted;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public WorkspaceRole Role { get; private set; }
    public string PermissionKey { get; private set; } = string.Empty;
    public bool IsGranted { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public void SetGranted(bool isGranted)
    {
        IsGranted = isGranted;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class PlatformRole
{
    private PlatformRole() { }

    public PlatformRole(string name, string? description, string permissionsJson)
    {
        Update(name, description, permissionsJson);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public string PermissionsJson { get; private set; } = "[]";
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public void Update(string name, string? description, string permissionsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(permissionsJson);

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        PermissionsJson = permissionsJson.Trim();
    }
}

public sealed class PlanDefinition
{
    private PlanDefinition() { }

    public PlanDefinition(
        PlanTier plan,
        string name,
        int monthlyMessageLimit,
        long storageLimitBytes,
        int maxSeats,
        string allowedFeaturesJson,
        long maxFileSizeBytes = 10L * 1024 * 1024,
        int maxDocumentCount = 25,
        int maxUrlSourceCount = 1,
        int playgroundMonthlyLimit = 200,
        decimal monthlyPriceTry = 0m)
    {
        if (!Enum.IsDefined(plan)) throw new ArgumentOutOfRangeException(nameof(plan));
        Plan = plan;
        Update(
            name,
            monthlyMessageLimit,
            storageLimitBytes,
            maxSeats,
            allowedFeaturesJson,
            maxFileSizeBytes,
            maxDocumentCount,
            maxUrlSourceCount,
            playgroundMonthlyLimit,
            monthlyPriceTry);
    }

    public PlanTier Plan { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public int MonthlyMessageLimit { get; private set; }
    public long StorageLimitBytes { get; private set; }
    public long MaxFileSizeBytes { get; private set; }
    public int MaxDocumentCount { get; private set; }
    public int MaxUrlSourceCount { get; private set; }
    public int PlaygroundMonthlyLimit { get; private set; } = 200;
    public int MaxSeats { get; private set; }
    public decimal MonthlyPriceTry { get; private set; }
    public string AllowedFeaturesJson { get; private set; } = "[]";
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public void Update(
        string name,
        int monthlyMessageLimit,
        long storageLimitBytes,
        int maxSeats,
        string allowedFeaturesJson,
        long maxFileSizeBytes = 10L * 1024 * 1024,
        int maxDocumentCount = 25,
        int maxUrlSourceCount = 1,
        int playgroundMonthlyLimit = 200,
        decimal monthlyPriceTry = 0m)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(allowedFeaturesJson);
        if (monthlyMessageLimit < 0) throw new ArgumentOutOfRangeException(nameof(monthlyMessageLimit));
        if (storageLimitBytes < 0) throw new ArgumentOutOfRangeException(nameof(storageLimitBytes));
        if (maxFileSizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(maxFileSizeBytes));
        if (maxDocumentCount < 0) throw new ArgumentOutOfRangeException(nameof(maxDocumentCount));
        if (maxUrlSourceCount < 0) throw new ArgumentOutOfRangeException(nameof(maxUrlSourceCount));
        if (playgroundMonthlyLimit < 0) throw new ArgumentOutOfRangeException(nameof(playgroundMonthlyLimit));
        if (maxSeats < 1) throw new ArgumentOutOfRangeException(nameof(maxSeats));
        if (monthlyPriceTry < 0) throw new ArgumentOutOfRangeException(nameof(monthlyPriceTry));

        Name = name.Trim();
        MonthlyMessageLimit = monthlyMessageLimit;
        StorageLimitBytes = storageLimitBytes;
        MaxFileSizeBytes = maxFileSizeBytes;
        MaxDocumentCount = maxDocumentCount;
        MaxUrlSourceCount = maxUrlSourceCount;
        PlaygroundMonthlyLimit = playgroundMonthlyLimit;
        MaxSeats = maxSeats;
        MonthlyPriceTry = monthlyPriceTry;
        AllowedFeaturesJson = allowedFeaturesJson.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

public sealed class WizardCatalogItem
{
    public static readonly string[] ValidCategories =
    [
        "USE_CASE",
        "ASSISTANT_ROLE",
        "PRIMARY_CHANNEL",
        "KNOWLEDGE_METHOD"
    ];

    private WizardCatalogItem() { }

    public WizardCatalogItem(
        string category,
        string key,
        string title,
        string description,
        string iconName,
        int sortOrder,
        bool isActive = true,
        string? defaultWelcomeMessage = null,
        string? defaultBusinessSummary = null,
        string? defaultKeyFaq = null,
        string? systemPromptHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        Category = category.Trim().ToUpperInvariant();
        Key = key.Trim().ToLowerInvariant();
        Update(
            title,
            description,
            iconName,
            sortOrder,
            isActive,
            defaultWelcomeMessage,
            defaultBusinessSummary,
            defaultKeyFaq,
            systemPromptHint);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public string Category { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string IconName { get; private set; } = "Sparkles";
    public string? DefaultWelcomeMessage { get; private set; }
    public string? DefaultBusinessSummary { get; private set; }
    public string? DefaultKeyFaq { get; private set; }
    public string? SystemPromptHint { get; private set; }
    public int SortOrder { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public void Update(
        string title,
        string description,
        string iconName,
        int sortOrder,
        bool isActive,
        string? defaultWelcomeMessage = null,
        string? defaultBusinessSummary = null,
        string? defaultKeyFaq = null,
        string? systemPromptHint = null,
        string? category = null,
        string? key = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(iconName);

        if (!string.IsNullOrWhiteSpace(category))
        {
            Category = category.Trim().ToUpperInvariant();
        }
        if (!string.IsNullOrWhiteSpace(key))
        {
            Key = key.Trim().ToLowerInvariant();
        }

        Title = title.Trim();
        Description = description.Trim();
        IconName = iconName.Trim();
        SortOrder = sortOrder;
        IsActive = isActive;
        DefaultWelcomeMessage = string.IsNullOrWhiteSpace(defaultWelcomeMessage) ? null : defaultWelcomeMessage.Trim();
        DefaultBusinessSummary = string.IsNullOrWhiteSpace(defaultBusinessSummary) ? null : defaultBusinessSummary.Trim();
        DefaultKeyFaq = string.IsNullOrWhiteSpace(defaultKeyFaq) ? null : defaultKeyFaq.Trim();
        SystemPromptHint = string.IsNullOrWhiteSpace(systemPromptHint) ? null : systemPromptHint.Trim();
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}

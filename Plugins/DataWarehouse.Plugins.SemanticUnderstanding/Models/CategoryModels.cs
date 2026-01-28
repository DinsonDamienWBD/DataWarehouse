using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Models
{
    /// <summary>
    /// Result of automatic categorization.
    /// </summary>
    public sealed record CategorizationResult
    {
        /// <summary>Primary category assigned.</summary>
        public string PrimaryCategory { get; init; } = string.Empty;

        /// <summary>Confidence score for primary category (0-1).</summary>
        public float PrimaryConfidence { get; init; }

        /// <summary>Secondary categories with confidence scores.</summary>
        public Dictionary<string, float> SecondaryCategories { get; init; } = new();

        /// <summary>Full category path in hierarchy (e.g., "Documents/Legal/Contracts").</summary>
        public string? CategoryPath { get; init; }

        /// <summary>Category hierarchy level (0 = root).</summary>
        public int HierarchyLevel { get; init; }

        /// <summary>Key features that influenced the categorization.</summary>
        public List<string> InfluencingFeatures { get; init; } = new();

        /// <summary>Whether this was AI-classified or rule-based.</summary>
        public CategorizationMethod Method { get; init; }

        /// <summary>Processing time for categorization.</summary>
        public TimeSpan Duration { get; init; }
    }

    /// <summary>
    /// Categorization method used.
    /// </summary>
    public enum CategorizationMethod
    {
        /// <summary>Rule-based classification.</summary>
        RuleBased,
        /// <summary>Statistical ML classification.</summary>
        Statistical,
        /// <summary>AI/LLM-based classification.</summary>
        AI,
        /// <summary>Hybrid of multiple methods.</summary>
        Hybrid
    }

    /// <summary>
    /// Category definition for custom categories.
    /// </summary>
    public sealed class CategoryDefinition
    {
        /// <summary>Unique category identifier.</summary>
        public required string Id { get; init; }

        /// <summary>Human-readable name.</summary>
        public required string Name { get; init; }

        /// <summary>Category description.</summary>
        public string? Description { get; init; }

        /// <summary>Parent category ID (null for root categories).</summary>
        public string? ParentId { get; init; }

        /// <summary>Keywords associated with this category.</summary>
        public List<string> Keywords { get; init; } = new();

        /// <summary>Regex patterns that match this category.</summary>
        public List<string> Patterns { get; init; } = new();

        /// <summary>Embedding vector for semantic matching.</summary>
        public float[]? Embedding { get; init; }

        /// <summary>Minimum confidence threshold for this category.</summary>
        public float MinConfidenceThreshold { get; init; } = 0.5f;

        /// <summary>Child category IDs.</summary>
        public List<string> ChildIds { get; init; } = new();

        /// <summary>Example documents for training.</summary>
        public List<CategoryExample> Examples { get; init; } = new();
    }

    /// <summary>
    /// Training example for a category.
    /// </summary>
    public sealed class CategoryExample
    {
        /// <summary>Example text content.</summary>
        public required string Text { get; init; }

        /// <summary>Pre-computed embedding for the example.</summary>
        public float[]? Embedding { get; init; }

        /// <summary>Weight of this example (higher = more influential).</summary>
        public float Weight { get; init; } = 1.0f;
    }

    /// <summary>
    /// Category training request.
    /// </summary>
    public sealed class CategoryTrainingRequest
    {
        /// <summary>Category ID to train.</summary>
        public string CategoryId { get; init; } = string.Empty;

        /// <summary>Example documents for training.</summary>
        public List<string> Examples { get; init; } = new();

        /// <summary>Whether to also update parent/child categories.</summary>
        public bool PropagateToHierarchy { get; init; }
    }

    /// <summary>
    /// Manages category hierarchy and definitions.
    /// </summary>
    public sealed class CategoryHierarchy
    {
        private readonly ConcurrentDictionary<string, CategoryDefinition> _categories = new();
        private readonly ConcurrentDictionary<string, List<string>> _childrenMap = new();

        /// <summary>Root category IDs.</summary>
        public IEnumerable<string> RootCategories =>
            _categories.Values.Where(c => c.ParentId == null).Select(c => c.Id);

        /// <summary>Total category count.</summary>
        public int Count => _categories.Count;

        /// <summary>
        /// Adds or updates a category.
        /// </summary>
        public void AddCategory(CategoryDefinition category)
        {
            _categories[category.Id] = category;

            if (category.ParentId != null)
            {
                _childrenMap.AddOrUpdate(
                    category.ParentId,
                    _ => new List<string> { category.Id },
                    (_, list) => { if (!list.Contains(category.Id)) list.Add(category.Id); return list; });
            }
        }

        /// <summary>
        /// Gets a category by ID.
        /// </summary>
        public CategoryDefinition? GetCategory(string id) =>
            _categories.TryGetValue(id, out var cat) ? cat : null;

        /// <summary>
        /// Gets all categories.
        /// </summary>
        public IEnumerable<CategoryDefinition> GetAllCategories() => _categories.Values;

        /// <summary>
        /// Gets child categories.
        /// </summary>
        public IEnumerable<CategoryDefinition> GetChildren(string parentId)
        {
            if (_childrenMap.TryGetValue(parentId, out var children))
            {
                foreach (var childId in children)
                {
                    if (_categories.TryGetValue(childId, out var child))
                        yield return child;
                }
            }
        }

        /// <summary>
        /// Gets the full path to a category.
        /// </summary>
        public string GetCategoryPath(string categoryId)
        {
            var path = new List<string>();
            var current = GetCategory(categoryId);

            while (current != null)
            {
                path.Insert(0, current.Name);
                current = current.ParentId != null ? GetCategory(current.ParentId) : null;
            }

            return string.Join("/", path);
        }

        /// <summary>
        /// Gets hierarchy level for a category.
        /// </summary>
        public int GetHierarchyLevel(string categoryId)
        {
            var level = 0;
            var current = GetCategory(categoryId);

            while (current?.ParentId != null)
            {
                level++;
                current = GetCategory(current.ParentId);
            }

            return level;
        }

        /// <summary>
        /// Removes a category and its children.
        /// </summary>
        public void RemoveCategory(string categoryId)
        {
            if (_categories.TryRemove(categoryId, out var category))
            {
                // Remove from parent's children list
                if (category.ParentId != null && _childrenMap.TryGetValue(category.ParentId, out var siblings))
                {
                    siblings.Remove(categoryId);
                }

                // Remove children recursively
                if (_childrenMap.TryRemove(categoryId, out var children))
                {
                    foreach (var childId in children)
                    {
                        RemoveCategory(childId);
                    }
                }
            }
        }

        /// <summary>
        /// Initializes with default categories.
        /// </summary>
        public void InitializeDefaults()
        {
            // Document types
            AddCategory(new CategoryDefinition { Id = "documents", Name = "Documents", Keywords = new() { "document", "file", "paper" } });
            AddCategory(new CategoryDefinition { Id = "documents.legal", Name = "Legal", ParentId = "documents", Keywords = new() { "contract", "agreement", "legal", "law", "attorney", "court" } });
            AddCategory(new CategoryDefinition { Id = "documents.financial", Name = "Financial", ParentId = "documents", Keywords = new() { "invoice", "receipt", "payment", "financial", "budget", "expense" } });
            AddCategory(new CategoryDefinition { Id = "documents.technical", Name = "Technical", ParentId = "documents", Keywords = new() { "technical", "specification", "api", "documentation", "manual" } });
            AddCategory(new CategoryDefinition { Id = "documents.hr", Name = "HR", ParentId = "documents", Keywords = new() { "employee", "resume", "cv", "hiring", "hr", "human resources", "benefits" } });

            // Media
            AddCategory(new CategoryDefinition { Id = "media", Name = "Media", Keywords = new() { "media", "image", "video", "audio" } });
            AddCategory(new CategoryDefinition { Id = "media.images", Name = "Images", ParentId = "media", Keywords = new() { "image", "photo", "picture", "screenshot" } });
            AddCategory(new CategoryDefinition { Id = "media.videos", Name = "Videos", ParentId = "media", Keywords = new() { "video", "movie", "clip", "recording" } });
            AddCategory(new CategoryDefinition { Id = "media.audio", Name = "Audio", ParentId = "media", Keywords = new() { "audio", "music", "podcast", "recording", "voice" } });

            // Communications
            AddCategory(new CategoryDefinition { Id = "communications", Name = "Communications", Keywords = new() { "email", "message", "chat", "communication" } });
            AddCategory(new CategoryDefinition { Id = "communications.email", Name = "Email", ParentId = "communications", Keywords = new() { "email", "mail", "inbox", "outbox" } });
            AddCategory(new CategoryDefinition { Id = "communications.chat", Name = "Chat", ParentId = "communications", Keywords = new() { "chat", "message", "instant", "slack", "teams" } });

            // Code
            AddCategory(new CategoryDefinition { Id = "code", Name = "Code", Keywords = new() { "code", "source", "programming", "software" } });
            AddCategory(new CategoryDefinition { Id = "code.source", Name = "Source Code", ParentId = "code", Keywords = new() { "code", "function", "class", "method", "variable" } });
            AddCategory(new CategoryDefinition { Id = "code.config", Name = "Configuration", ParentId = "code", Keywords = new() { "config", "configuration", "settings", "json", "yaml", "xml" } });

            // Data
            AddCategory(new CategoryDefinition { Id = "data", Name = "Data", Keywords = new() { "data", "database", "dataset", "records" } });
            AddCategory(new CategoryDefinition { Id = "data.structured", Name = "Structured Data", ParentId = "data", Keywords = new() { "csv", "excel", "spreadsheet", "table", "database" } });
            AddCategory(new CategoryDefinition { Id = "data.logs", Name = "Logs", ParentId = "data", Keywords = new() { "log", "logging", "trace", "debug", "error" } });
        }
    }
}

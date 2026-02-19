using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags;

/// <summary>
/// Provides a single entry point for creating and wiring all tag subsystem components.
/// Returns a <see cref="TagServiceCollection"/> record containing all seven subsystems
/// (schema registry, tag store, tag index, attachment service, propagation engine,
/// policy engine, query API) pre-wired with correct dependencies.
/// </summary>
/// <remarks>
/// <para>
/// Usage:
/// <code>
/// var services = TagServiceRegistration.CreateDefault();
/// // All subsystems are ready to use:
/// await services.AttachmentService.AttachAsync(objectKey, tagKey, value, source);
/// var result = await services.QueryApi.QueryAsync(request);
/// </code>
/// </para>
/// <para>
/// The optional <c>messageBus</c> parameter on <see cref="CreateDefault"/> enables event publishing
/// for attachment and policy violation events. When null, services operate without
/// event emission (suitable for tests and isolated deployments).
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag service registration")]
public static class TagServiceRegistration
{
    /// <summary>
    /// Creates a fully wired <see cref="TagServiceCollection"/> with default in-memory implementations
    /// for all seven tag subsystems.
    /// </summary>
    /// <param name="messageBus">
    /// Optional message bus for event publishing. When provided, tag attachment events
    /// and policy violation events are published. When null, events are silently discarded.
    /// </param>
    /// <returns>A <see cref="TagServiceCollection"/> with all subsystems wired together.</returns>
    public static TagServiceCollection CreateDefault(IMessageBus? messageBus = null)
    {
        var schemaRegistry = new InMemoryTagSchemaRegistry();
        var tagStore = new InMemoryTagStore();
        var tagIndex = new InvertedTagIndex();
        var attachmentService = new DefaultTagAttachmentService(tagStore, schemaRegistry, messageBus);
        var propagationEngine = new DefaultTagPropagationEngine(attachmentService, schemaRegistry);
        var policyEngine = new DefaultTagPolicyEngine(messageBus);
        var queryApi = new DefaultTagQueryApi(tagIndex, tagStore);

        return new TagServiceCollection(
            SchemaRegistry: schemaRegistry,
            TagStore: tagStore,
            TagIndex: tagIndex,
            AttachmentService: attachmentService,
            PropagationEngine: propagationEngine,
            PolicyEngine: policyEngine,
            QueryApi: queryApi
        );
    }
}

/// <summary>
/// Immutable record containing all seven tag subsystem components, fully wired together.
/// Created by <see cref="TagServiceRegistration.CreateDefault"/>.
/// </summary>
/// <param name="SchemaRegistry">The tag schema registry for schema governance.</param>
/// <param name="TagStore">The tag store for persistence.</param>
/// <param name="TagIndex">The inverted tag index for tag-to-object lookups.</param>
/// <param name="AttachmentService">The attachment service for tag CRUD operations.</param>
/// <param name="PropagationEngine">The propagation engine for pipeline stage transitions.</param>
/// <param name="PolicyEngine">The policy engine for compliance enforcement.</param>
/// <param name="QueryApi">The query API for expression-based tag queries.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 55: Tag service collection")]
public record TagServiceCollection(
    ITagSchemaRegistry SchemaRegistry,
    ITagStore TagStore,
    ITagIndex TagIndex,
    ITagAttachmentService AttachmentService,
    ITagPropagationEngine PropagationEngine,
    ITagPolicyEngine PolicyEngine,
    ITagQueryApi QueryApi);

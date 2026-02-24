using System;
using System.Collections.Generic;
using System.Text;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

// =============================================================================
// Helm Values Record
// =============================================================================

/// <summary>
/// Default Helm chart values for a DataWarehouse Kubernetes deployment.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Helm chart (ECOS-15)")]
public sealed record HelmValues
{
    /// <summary>Number of StatefulSet replicas (1 for single-node, 3+ for clustered).</summary>
    public int ReplicaCount { get; init; } = 1;

    /// <summary>Container image repository.</summary>
    public string ImageRepository { get; init; } = "datawarehouse/datawarehouse";

    /// <summary>Container image tag.</summary>
    public string ImageTag { get; init; } = "6.0.0";

    /// <summary>Image pull policy.</summary>
    public string ImagePullPolicy { get; init; } = "IfNotPresent";

    /// <summary>CPU request.</summary>
    public string ResourceRequestCpu { get; init; } = "500m";

    /// <summary>Memory request.</summary>
    public string ResourceRequestMemory { get; init; } = "1Gi";

    /// <summary>CPU limit.</summary>
    public string ResourceLimitCpu { get; init; } = "4";

    /// <summary>Memory limit.</summary>
    public string ResourceLimitMemory { get; init; } = "8Gi";

    /// <summary>Persistent volume size.</summary>
    public string StorageSize { get; init; } = "100Gi";

    /// <summary>Storage class name (empty for default).</summary>
    public string StorageClass { get; init; } = "";

    /// <summary>Kubernetes service type.</summary>
    public string ServiceType { get; init; } = "ClusterIP";

    /// <summary>Service port (PostgreSQL wire protocol).</summary>
    public int ServicePort { get; init; } = 5432;

    /// <summary>Whether Ingress is enabled.</summary>
    public bool IngressEnabled { get; init; }

    /// <summary>Ingress hosts.</summary>
    public IReadOnlyList<string> IngressHosts { get; init; } = Array.Empty<string>();

    /// <summary>Ingress TLS configuration enabled.</summary>
    public bool IngressTlsEnabled { get; init; }

    /// <summary>Operational profile preset.</summary>
    public string OperationalProfile { get; init; } = "Balanced";

    /// <summary>Plugins to enable (["*"] for all).</summary>
    public IReadOnlyList<string> PluginsEnabled { get; init; } = new[] { "*" };

    /// <summary>Enable TLS for inter-node and client communication.</summary>
    public bool TlsEnabled { get; init; } = true;

    /// <summary>Kubernetes secret name containing TLS certificates (empty to auto-generate).</summary>
    public string TlsSecretName { get; init; } = "";

    /// <summary>Enable clustering mode (Raft consensus).</summary>
    public bool ClusteringEnabled { get; init; }

    /// <summary>Raft consensus port for cluster communication.</summary>
    public int ClusteringRaftPort { get; init; } = 8300;
}

// =============================================================================
// Helm Deployment Mode
// =============================================================================

/// <summary>
/// Supported Helm chart deployment modes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Helm chart (ECOS-15)")]
public enum HelmDeploymentMode
{
    /// <summary>Single replica, no headless service, no Raft.</summary>
    SingleNode = 0,

    /// <summary>3+ replicas, headless service, Raft consensus, pod anti-affinity.</summary>
    Clustered = 1
}

// =============================================================================
// Helm Chart Specification
// =============================================================================

/// <summary>
/// Defines the Helm chart for deploying DataWarehouse on Kubernetes.
/// Generates Chart.yaml, values.yaml, and all template files for StatefulSet,
/// Service, ConfigMap, Secret, Ingress, PVC, and test connectivity.
/// Supports single-node and clustered deployment modes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Helm chart (ECOS-15)")]
public static class HelmChartSpecification
{
    /// <summary>Helm chart name.</summary>
    public const string ChartName = "datawarehouse";

    /// <summary>Helm chart version.</summary>
    public const string ChartVersion = "1.0.0";

    /// <summary>Application version deployed by this chart.</summary>
    public const string AppVersion = "6.0.0";

    /// <summary>Chart description.</summary>
    public const string Description = "DataWarehouse -- AI-native data warehouse";

    /// <summary>
    /// Returns the default Helm values for the specified deployment mode.
    /// </summary>
    public static HelmValues GetDefaultValues(HelmDeploymentMode mode = HelmDeploymentMode.SingleNode) => mode switch
    {
        HelmDeploymentMode.Clustered => new HelmValues
        {
            ReplicaCount = 3,
            ClusteringEnabled = true,
            ClusteringRaftPort = 8300
        },
        _ => new HelmValues()
    };

    /// <summary>
    /// Generates all Helm chart template files as a dictionary of
    /// relative file paths to YAML/template content.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GenerateChartTemplates()
    {
        var files = new Dictionary<string, string>(16);

        files["Chart.yaml"] = GenerateChartYaml();
        files["values.yaml"] = GenerateValuesYaml();
        files["templates/_helpers.tpl"] = GenerateHelpersTpl();
        files["templates/statefulset.yaml"] = GenerateStatefulSetYaml();
        files["templates/service.yaml"] = GenerateServiceYaml();
        files["templates/service-headless.yaml"] = GenerateHeadlessServiceYaml();
        files["templates/configmap.yaml"] = GenerateConfigMapYaml();
        files["templates/secret.yaml"] = GenerateSecretYaml();
        files["templates/ingress.yaml"] = GenerateIngressYaml();
        files["templates/pvc.yaml"] = GeneratePvcYaml();
        files["templates/tests/test-connection.yaml"] = GenerateTestConnectionYaml();
        files["templates/NOTES.txt"] = GenerateNotesTxt();

        return files;
    }

    // -------------------------------------------------------------------------
    // Chart.yaml
    // -------------------------------------------------------------------------

    private static string GenerateChartYaml()
    {
        var sb = new StringBuilder(256);
        sb.AppendLine("apiVersion: v2");
        sb.Append("name: ").AppendLine(ChartName);
        sb.Append("description: ").AppendLine(Description);
        sb.AppendLine("type: application");
        sb.Append("version: ").AppendLine(ChartVersion);
        sb.Append("appVersion: \"").Append(AppVersion).AppendLine("\"");
        sb.AppendLine("keywords:");
        sb.AppendLine("  - datawarehouse");
        sb.AppendLine("  - database");
        sb.AppendLine("  - storage");
        sb.AppendLine("  - ai");
        sb.AppendLine("maintainers:");
        sb.AppendLine("  - name: DataWarehouse Team");
        sb.AppendLine("    email: team@datawarehouse.io");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // values.yaml
    // -------------------------------------------------------------------------

    private static string GenerateValuesYaml()
    {
        var defaults = new HelmValues();
        var sb = new StringBuilder(1024);
        sb.AppendLine("# DataWarehouse Helm Chart — Default Values");
        sb.AppendLine();
        sb.Append("replicaCount: ").Append(defaults.ReplicaCount).AppendLine();
        sb.AppendLine();
        sb.AppendLine("image:");
        sb.Append("  repository: ").AppendLine(defaults.ImageRepository);
        sb.Append("  tag: \"").Append(defaults.ImageTag).AppendLine("\"");
        sb.Append("  pullPolicy: ").AppendLine(defaults.ImagePullPolicy);
        sb.AppendLine();
        sb.AppendLine("resources:");
        sb.AppendLine("  requests:");
        sb.Append("    cpu: \"").Append(defaults.ResourceRequestCpu).AppendLine("\"");
        sb.Append("    memory: \"").Append(defaults.ResourceRequestMemory).AppendLine("\"");
        sb.AppendLine("  limits:");
        sb.Append("    cpu: \"").Append(defaults.ResourceLimitCpu).AppendLine("\"");
        sb.Append("    memory: \"").Append(defaults.ResourceLimitMemory).AppendLine("\"");
        sb.AppendLine();
        sb.AppendLine("storage:");
        sb.Append("  size: \"").Append(defaults.StorageSize).AppendLine("\"");
        sb.Append("  storageClass: \"").Append(defaults.StorageClass).AppendLine("\"");
        sb.AppendLine();
        sb.AppendLine("service:");
        sb.Append("  type: ").AppendLine(defaults.ServiceType);
        sb.Append("  port: ").Append(defaults.ServicePort).AppendLine();
        sb.AppendLine();
        sb.AppendLine("ingress:");
        sb.Append("  enabled: ").AppendLine(defaults.IngressEnabled ? "true" : "false");
        sb.AppendLine("  hosts: []");
        sb.AppendLine("  tls:");
        sb.Append("    enabled: ").AppendLine(defaults.IngressTlsEnabled ? "true" : "false");
        sb.AppendLine();
        sb.Append("operationalProfile: \"").Append(defaults.OperationalProfile).AppendLine("\"");
        sb.AppendLine();
        sb.AppendLine("plugins:");
        sb.AppendLine("  enabled:");
        sb.AppendLine("    - \"*\"");
        sb.AppendLine();
        sb.AppendLine("tls:");
        sb.Append("  enabled: ").AppendLine(defaults.TlsEnabled ? "true" : "false");
        sb.Append("  secretName: \"").Append(defaults.TlsSecretName).AppendLine("\"");
        sb.AppendLine();
        sb.AppendLine("clustering:");
        sb.Append("  enabled: ").AppendLine(defaults.ClusteringEnabled ? "true" : "false");
        sb.Append("  raftPort: ").Append(defaults.ClusteringRaftPort).AppendLine();
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/_helpers.tpl
    // -------------------------------------------------------------------------

    private static string GenerateHelpersTpl()
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("{{/*");
        sb.AppendLine("Expand the name of the chart.");
        sb.AppendLine("*/}}");
        sb.AppendLine("{{- define \"datawarehouse.name\" -}}");
        sb.AppendLine("{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix \"-\" }}");
        sb.AppendLine("{{- end }}");
        sb.AppendLine();
        sb.AppendLine("{{/*");
        sb.AppendLine("Create a default fully qualified app name.");
        sb.AppendLine("*/}}");
        sb.AppendLine("{{- define \"datawarehouse.fullname\" -}}");
        sb.AppendLine("{{- if .Values.fullnameOverride }}");
        sb.AppendLine("{{- .Values.fullnameOverride | trunc 63 | trimSuffix \"-\" }}");
        sb.AppendLine("{{- else }}");
        sb.AppendLine("{{- $name := default .Chart.Name .Values.nameOverride }}");
        sb.AppendLine("{{- if contains $name .Release.Name }}");
        sb.AppendLine("{{- .Release.Name | trunc 63 | trimSuffix \"-\" }}");
        sb.AppendLine("{{- else }}");
        sb.AppendLine("{{- printf \"%s-%s\" .Release.Name $name | trunc 63 | trimSuffix \"-\" }}");
        sb.AppendLine("{{- end }}");
        sb.AppendLine("{{- end }}");
        sb.AppendLine("{{- end }}");
        sb.AppendLine();
        sb.AppendLine("{{/*");
        sb.AppendLine("Common labels");
        sb.AppendLine("*/}}");
        sb.AppendLine("{{- define \"datawarehouse.labels\" -}}");
        sb.AppendLine("helm.sh/chart: {{ include \"datawarehouse.name\" . }}-{{ .Chart.Version | replace \"+\" \"_\" }}");
        sb.AppendLine("{{ include \"datawarehouse.selectorLabels\" . }}");
        sb.AppendLine("app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}");
        sb.AppendLine("app.kubernetes.io/managed-by: {{ .Release.Service }}");
        sb.AppendLine("{{- end }}");
        sb.AppendLine();
        sb.AppendLine("{{/*");
        sb.AppendLine("Selector labels");
        sb.AppendLine("*/}}");
        sb.AppendLine("{{- define \"datawarehouse.selectorLabels\" -}}");
        sb.AppendLine("app.kubernetes.io/name: {{ include \"datawarehouse.name\" . }}");
        sb.AppendLine("app.kubernetes.io/instance: {{ .Release.Name }}");
        sb.AppendLine("{{- end }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/statefulset.yaml
    // -------------------------------------------------------------------------

    private static string GenerateStatefulSetYaml()
    {
        var sb = new StringBuilder(2048);
        sb.AppendLine("apiVersion: apps/v1");
        sb.AppendLine("kind: StatefulSet");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("spec:");
        sb.AppendLine("  replicas: {{ .Values.replicaCount }}");
        sb.AppendLine("  serviceName: {{ include \"datawarehouse.fullname\" . }}-headless");
        sb.AppendLine("  selector:");
        sb.AppendLine("    matchLabels:");
        sb.AppendLine("      {{- include \"datawarehouse.selectorLabels\" . | nindent 6 }}");
        sb.AppendLine("  template:");
        sb.AppendLine("    metadata:");
        sb.AppendLine("      labels:");
        sb.AppendLine("        {{- include \"datawarehouse.selectorLabels\" . | nindent 8 }}");
        sb.AppendLine("    spec:");
        // Pod anti-affinity for clustered mode
        sb.AppendLine("      {{- if .Values.clustering.enabled }}");
        sb.AppendLine("      affinity:");
        sb.AppendLine("        podAntiAffinity:");
        sb.AppendLine("          preferredDuringSchedulingIgnoredDuringExecution:");
        sb.AppendLine("            - weight: 100");
        sb.AppendLine("              podAffinityTerm:");
        sb.AppendLine("                labelSelector:");
        sb.AppendLine("                  matchExpressions:");
        sb.AppendLine("                    - key: app.kubernetes.io/name");
        sb.AppendLine("                      operator: In");
        sb.AppendLine("                      values:");
        sb.AppendLine("                        - {{ include \"datawarehouse.name\" . }}");
        sb.AppendLine("                topologyKey: kubernetes.io/hostname");
        sb.AppendLine("      {{- end }}");
        sb.AppendLine("      containers:");
        sb.AppendLine("        - name: {{ .Chart.Name }}");
        sb.AppendLine("          image: \"{{ .Values.image.repository }}:{{ .Values.image.tag }}\"");
        sb.AppendLine("          imagePullPolicy: {{ .Values.image.pullPolicy }}");
        sb.AppendLine("          ports:");
        sb.AppendLine("            - name: dw");
        sb.AppendLine("              containerPort: {{ .Values.service.port }}");
        sb.AppendLine("              protocol: TCP");
        sb.AppendLine("            {{- if .Values.clustering.enabled }}");
        sb.AppendLine("            - name: raft");
        sb.AppendLine("              containerPort: {{ .Values.clustering.raftPort }}");
        sb.AppendLine("              protocol: TCP");
        sb.AppendLine("            {{- end }}");
        sb.AppendLine("          env:");
        sb.AppendLine("            - name: DW_OPERATIONAL_PROFILE");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                configMapKeyRef:");
        sb.AppendLine("                  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("                  key: operational-profile");
        sb.AppendLine("            - name: DW_ADMIN_PASSWORD");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                secretKeyRef:");
        sb.AppendLine("                  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("                  key: admin-password");
        sb.AppendLine("            {{- if .Values.clustering.enabled }}");
        sb.AppendLine("            - name: DW_CLUSTER_ENABLED");
        sb.AppendLine("              value: \"true\"");
        sb.AppendLine("            - name: DW_RAFT_PORT");
        sb.AppendLine("              value: \"{{ .Values.clustering.raftPort }}\"");
        sb.AppendLine("            - name: DW_POD_NAME");
        sb.AppendLine("              valueFrom:");
        sb.AppendLine("                fieldRef:");
        sb.AppendLine("                  fieldPath: metadata.name");
        sb.AppendLine("            - name: DW_HEADLESS_SERVICE");
        sb.AppendLine("              value: {{ include \"datawarehouse.fullname\" . }}-headless");
        sb.AppendLine("            {{- end }}");
        sb.AppendLine("          readinessProbe:");
        sb.AppendLine("            tcpSocket:");
        sb.AppendLine("              port: dw");
        sb.AppendLine("            initialDelaySeconds: 10");
        sb.AppendLine("            periodSeconds: 5");
        sb.AppendLine("          livenessProbe:");
        sb.AppendLine("            tcpSocket:");
        sb.AppendLine("              port: dw");
        sb.AppendLine("            initialDelaySeconds: 30");
        sb.AppendLine("            periodSeconds: 10");
        sb.AppendLine("          resources:");
        sb.AppendLine("            requests:");
        sb.AppendLine("              cpu: {{ .Values.resources.requests.cpu }}");
        sb.AppendLine("              memory: {{ .Values.resources.requests.memory }}");
        sb.AppendLine("            limits:");
        sb.AppendLine("              cpu: {{ .Values.resources.limits.cpu }}");
        sb.AppendLine("              memory: {{ .Values.resources.limits.memory }}");
        sb.AppendLine("          volumeMounts:");
        sb.AppendLine("            - name: data");
        sb.AppendLine("              mountPath: /var/lib/datawarehouse");
        sb.AppendLine("            {{- if .Values.tls.enabled }}");
        sb.AppendLine("            - name: tls");
        sb.AppendLine("              mountPath: /etc/datawarehouse/tls");
        sb.AppendLine("              readOnly: true");
        sb.AppendLine("            {{- end }}");
        sb.AppendLine("      {{- if .Values.tls.enabled }}");
        sb.AppendLine("      volumes:");
        sb.AppendLine("        - name: tls");
        sb.AppendLine("          secret:");
        sb.AppendLine("            secretName: {{ .Values.tls.secretName | default (printf \"%s-tls\" (include \"datawarehouse.fullname\" .)) }}");
        sb.AppendLine("      {{- end }}");
        sb.AppendLine("  volumeClaimTemplates:");
        sb.AppendLine("    - metadata:");
        sb.AppendLine("        name: data");
        sb.AppendLine("      spec:");
        sb.AppendLine("        accessModes: [ \"ReadWriteOnce\" ]");
        sb.AppendLine("        {{- if .Values.storage.storageClass }}");
        sb.AppendLine("        storageClassName: {{ .Values.storage.storageClass }}");
        sb.AppendLine("        {{- end }}");
        sb.AppendLine("        resources:");
        sb.AppendLine("          requests:");
        sb.AppendLine("            storage: {{ .Values.storage.size }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/service.yaml
    // -------------------------------------------------------------------------

    private static string GenerateServiceYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("spec:");
        sb.AppendLine("  type: {{ .Values.service.type }}");
        sb.AppendLine("  ports:");
        sb.AppendLine("    - port: {{ .Values.service.port }}");
        sb.AppendLine("      targetPort: dw");
        sb.AppendLine("      protocol: TCP");
        sb.AppendLine("      name: dw");
        sb.AppendLine("  selector:");
        sb.AppendLine("    {{- include \"datawarehouse.selectorLabels\" . | nindent 4 }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/service-headless.yaml
    // -------------------------------------------------------------------------

    private static string GenerateHeadlessServiceYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("{{- if .Values.clustering.enabled }}");
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Service");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: {{ include \"datawarehouse.fullname\" . }}-headless");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("spec:");
        sb.AppendLine("  type: ClusterIP");
        sb.AppendLine("  clusterIP: None");
        sb.AppendLine("  ports:");
        sb.AppendLine("    - port: {{ .Values.service.port }}");
        sb.AppendLine("      targetPort: dw");
        sb.AppendLine("      protocol: TCP");
        sb.AppendLine("      name: dw");
        sb.AppendLine("    - port: {{ .Values.clustering.raftPort }}");
        sb.AppendLine("      targetPort: raft");
        sb.AppendLine("      protocol: TCP");
        sb.AppendLine("      name: raft");
        sb.AppendLine("  selector:");
        sb.AppendLine("    {{- include \"datawarehouse.selectorLabels\" . | nindent 4 }}");
        sb.AppendLine("{{- end }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/configmap.yaml
    // -------------------------------------------------------------------------

    private static string GenerateConfigMapYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: ConfigMap");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("data:");
        sb.AppendLine("  operational-profile: {{ .Values.operationalProfile | quote }}");
        sb.AppendLine("  plugins-enabled: {{ .Values.plugins.enabled | join \",\" | quote }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/secret.yaml
    // -------------------------------------------------------------------------

    private static string GenerateSecretYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Secret");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("type: Opaque");
        sb.AppendLine("data:");
        sb.AppendLine("  admin-password: {{ .Values.adminPassword | default \"changeme\" | b64enc | quote }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/ingress.yaml
    // -------------------------------------------------------------------------

    private static string GenerateIngressYaml()
    {
        var sb = new StringBuilder(1024);
        sb.AppendLine("{{- if .Values.ingress.enabled }}");
        sb.AppendLine("apiVersion: networking.k8s.io/v1");
        sb.AppendLine("kind: Ingress");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("spec:");
        sb.AppendLine("  {{- if .Values.ingress.tls.enabled }}");
        sb.AppendLine("  tls:");
        sb.AppendLine("    - hosts:");
        sb.AppendLine("        {{- range .Values.ingress.hosts }}");
        sb.AppendLine("        - {{ . | quote }}");
        sb.AppendLine("        {{- end }}");
        sb.AppendLine("      secretName: {{ include \"datawarehouse.fullname\" . }}-ingress-tls");
        sb.AppendLine("  {{- end }}");
        sb.AppendLine("  rules:");
        sb.AppendLine("    {{- range .Values.ingress.hosts }}");
        sb.AppendLine("    - host: {{ . | quote }}");
        sb.AppendLine("      http:");
        sb.AppendLine("        paths:");
        sb.AppendLine("          - path: /");
        sb.AppendLine("            pathType: Prefix");
        sb.AppendLine("            backend:");
        sb.AppendLine("              service:");
        sb.AppendLine("                name: {{ include \"datawarehouse.fullname\" $ }}");
        sb.AppendLine("                port:");
        sb.AppendLine("                  number: {{ $.Values.service.port }}");
        sb.AppendLine("    {{- end }}");
        sb.AppendLine("{{- end }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/pvc.yaml
    // -------------------------------------------------------------------------

    private static string GeneratePvcYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("{{/* PVC is managed via StatefulSet volumeClaimTemplates. */}}");
        sb.AppendLine("{{/* This file exists for standalone PVC use cases. */}}");
        sb.AppendLine("{{- if not (eq .Values.replicaCount 0) }}");
        sb.AppendLine("# PersistentVolumeClaims are managed by the StatefulSet");
        sb.AppendLine("# volumeClaimTemplates section. See statefulset.yaml.");
        sb.AppendLine("#");
        sb.AppendLine("# For standalone PVC (e.g., pre-provisioned volumes):");
        sb.AppendLine("# apiVersion: v1");
        sb.AppendLine("# kind: PersistentVolumeClaim");
        sb.AppendLine("# metadata:");
        sb.AppendLine("#   name: {{ include \"datawarehouse.fullname\" . }}-data");
        sb.AppendLine("# spec:");
        sb.AppendLine("#   accessModes: [ \"ReadWriteOnce\" ]");
        sb.AppendLine("#   resources:");
        sb.AppendLine("#     requests:");
        sb.AppendLine("#       storage: {{ .Values.storage.size }}");
        sb.AppendLine("{{- end }}");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/tests/test-connection.yaml
    // -------------------------------------------------------------------------

    private static string GenerateTestConnectionYaml()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("apiVersion: v1");
        sb.AppendLine("kind: Pod");
        sb.AppendLine("metadata:");
        sb.AppendLine("  name: \"{{ include \"datawarehouse.fullname\" . }}-test-connection\"");
        sb.AppendLine("  labels:");
        sb.AppendLine("    {{- include \"datawarehouse.labels\" . | nindent 4 }}");
        sb.AppendLine("  annotations:");
        sb.AppendLine("    \"helm.sh/hook\": test");
        sb.AppendLine("spec:");
        sb.AppendLine("  restartPolicy: Never");
        sb.AppendLine("  containers:");
        sb.AppendLine("    - name: test");
        sb.AppendLine("      image: postgres:16-alpine");
        sb.AppendLine("      command:");
        sb.AppendLine("        - \"sh\"");
        sb.AppendLine("        - \"-c\"");
        sb.AppendLine("        - |");
        sb.AppendLine("          PGPASSWORD=$DW_ADMIN_PASSWORD psql \\");
        sb.AppendLine("            -h {{ include \"datawarehouse.fullname\" . }} \\");
        sb.AppendLine("            -p {{ .Values.service.port }} \\");
        sb.AppendLine("            -U admin \\");
        sb.AppendLine("            -c \"SELECT 1;\"");
        sb.AppendLine("      env:");
        sb.AppendLine("        - name: DW_ADMIN_PASSWORD");
        sb.AppendLine("          valueFrom:");
        sb.AppendLine("            secretKeyRef:");
        sb.AppendLine("              name: {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine("              key: admin-password");
        return sb.ToString();
    }

    // -------------------------------------------------------------------------
    // templates/NOTES.txt
    // -------------------------------------------------------------------------

    private static string GenerateNotesTxt()
    {
        var sb = new StringBuilder(512);
        sb.AppendLine("DataWarehouse has been deployed!");
        sb.AppendLine();
        sb.AppendLine("1. Get the service endpoint:");
        sb.AppendLine("   kubectl get svc {{ include \"datawarehouse.fullname\" . }}");
        sb.AppendLine();
        sb.AppendLine("2. Connect via psql:");
        sb.AppendLine("   export DW_PASSWORD=$(kubectl get secret {{ include \"datawarehouse.fullname\" . }} -o jsonpath=\"{.data.admin-password}\" | base64 -d)");
        sb.AppendLine("   kubectl port-forward svc/{{ include \"datawarehouse.fullname\" . }} {{ .Values.service.port }}:{{ .Values.service.port }}");
        sb.AppendLine("   PGPASSWORD=$DW_PASSWORD psql -h 127.0.0.1 -p {{ .Values.service.port }} -U admin");
        sb.AppendLine();
        sb.AppendLine("{{- if .Values.clustering.enabled }}");
        sb.AppendLine("3. Cluster mode enabled with {{ .Values.replicaCount }} replicas.");
        sb.AppendLine("   Raft consensus on port {{ .Values.clustering.raftPort }}.");
        sb.AppendLine("{{- end }}");
        sb.AppendLine();
        sb.AppendLine("4. Run tests:");
        sb.AppendLine("   helm test {{ .Release.Name }}");
        return sb.ToString();
    }
}

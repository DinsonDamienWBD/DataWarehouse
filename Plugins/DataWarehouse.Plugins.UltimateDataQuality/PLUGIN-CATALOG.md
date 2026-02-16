# UltimateDataQuality Plugin Catalog

## Overview

**Plugin ID:** `ultimate-data-quality`
**Version:** 3.0.0
**Category:** Data Management / Data Quality
**Total Strategies:** 63

The UltimateDataQuality plugin provides comprehensive data quality validation, profiling, cleansing, and scoring capabilities. It implements production-ready algorithms for statistical profiling (min/max/mean/stddev/percentiles), validation (regex/range/constraint checking), cleansing (normalization, null handling), duplicate detection (Levenshtein + Soundex), and quality scoring (weighted dimensions: completeness, accuracy, consistency, timeliness).

## Architecture

### Design Pattern
- **Strategy Pattern**: Quality checks exposed as discoverable strategies
- **Self-Contained**: All quality logic implemented within plugin
- **Message Bus Publishing**: Quality scores published to `quality.score.updated` for consumers
- **Streaming Algorithms**: Constant-space statistical calculations for large datasets

### Key Capabilities
1. **Validation**: Regex, range, constraint, cardinality, uniqueness checking
2. **Profiling**: Statistical analysis (min/max/mean/stddev/percentiles/cardinality)
3. **Cleansing**: Whitespace normalization, case standardization, null handling
4. **Duplicate Detection**: Levenshtein distance + Soundex fuzzy matching
5. **Standardization**: Address, phone, email normalization
6. **Quality Scoring**: Weighted dimension scoring (completeness, accuracy, consistency, timeliness)
7. **Monitoring**: Real-time quality metrics, drift detection
8. **Reporting**: Quality dashboards, trend analysis

## Strategy Categories

### 1. Validation Strategies (12 strategies)

| Strategy ID | Validation Type | Description |
|------------|-----------------|-------------|
| `validation-regex` | **Pattern Matching** | Regex validation against schema rules |
| `validation-range` | **Numeric Range** | Min/max bound checking |
| `validation-constraint` | **Business Rules** | Custom constraint evaluation |
| `validation-uniqueness` | **Unique Check** | Detect duplicate values in column |
| `validation-referential-integrity` | **FK Check** | Foreign key validation |
| `validation-cardinality` | **Count Limits** | Min/max row count |
| `validation-completeness` | **Null Check** | Non-null percentage threshold |
| `validation-enum` | **Allowed Values** | Check against whitelist |
| `validation-cross-field` | **Multi-Column** | Cross-field consistency |
| `validation-temporal` | **Date/Time** | Date range, format validation |
| `validation-custom` | **User-Defined** | Custom validation functions |
| `validation-ml` | **AI-Powered** | ML-based anomaly detection |

**Regex Validation:**
- **Email Pattern**: `^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$`
- **Phone Pattern**: `^\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}$`
- **SSN Pattern**: `^\d{3}-\d{2}-\d{4}$`
- **Credit Card**: Luhn algorithm + format check
- **Timeout**: 100ms per regex (SDK hardening)

**Range Validation:**
```csharp
public bool ValidateRange(decimal value, decimal min, decimal max)
{
    return value >= min && value <= max;
}

// Examples:
// Age: 0-120
// Temperature: -273.15 (absolute zero) to 1000
// Percentage: 0.0-100.0
```

### 2. Profiling Strategies (9 strategies)

| Strategy ID | Statistic | Description |
|------------|-----------|-------------|
| `profiling-basic-stats` | **Min/Max/Mean** | Basic statistical summary |
| `profiling-standard-deviation` | **StdDev/Variance** | Measure of spread |
| `profiling-percentiles` | **P50/P90/P95/P99** | Percentile distribution |
| `profiling-cardinality` | **Distinct Count** | Unique value count |
| `profiling-histogram` | **Distribution** | Value frequency histogram |
| `profiling-correlation` | **Pearson's r** | Column correlation analysis |
| `profiling-outlier-detection` | **IQR/Z-Score** | Detect outliers |
| `profiling-data-types` | **Type Inference** | Infer column data types |
| `profiling-null-analysis` | **Null Percentage** | Analyze null distribution |

**Statistical Profiling (Streaming Algorithms):**

**Welford's Algorithm (Online Mean & Variance):**
```csharp
// Constant space O(1), single pass O(n)
int count = 0;
double mean = 0;
double M2 = 0; // Sum of squares of differences

foreach (var value in data)
{
    count++;
    double delta = value - mean;
    mean += delta / count;
    double delta2 = value - mean;
    M2 += delta * delta2;
}

double variance = M2 / count;
double stddev = Math.Sqrt(variance);
```

**Percentile Estimation (P² Algorithm):**
- **Space**: O(1) - maintains 5 markers
- **Accuracy**: ±1% for P50, ±2% for P95/P99
- **Use Case**: Calculate percentiles on streams without buffering entire dataset

**Cardinality Estimation (HyperLogLog):**
- **Space**: O(log log n) - typically 1-2KB
- **Accuracy**: ±2% error
- **Use Case**: Estimate distinct count for billions of rows

### 3. Cleansing Strategies (8 strategies)

| Strategy ID | Transformation | Description |
|------------|----------------|-------------|
| `cleansing-whitespace` | **Trim** | Remove leading/trailing whitespace |
| `cleansing-case-standardization` | **Case Normalization** | Uppercase, lowercase, title case |
| `cleansing-null-handling` | **Null Replacement** | Replace nulls with defaults |
| `cleansing-date-normalization` | **Date Format** | Standardize date formats |
| `cleansing-remove-duplicates` | **Dedup** | Remove duplicate rows |
| `cleansing-outlier-removal` | **Outlier Handling** | Cap or remove outliers |
| `cleansing-string-normalization` | **Unicode** | Normalize Unicode (NFC, NFD) |
| `cleansing-data-repair` | **Auto-Fix** | Automatically repair common errors |

**Whitespace Normalization:**
```csharp
// Multi-stage normalization
string CleanseWhitespace(string input)
{
    if (string.IsNullOrWhiteSpace(input)) return string.Empty;

    // 1. Trim leading/trailing
    input = input.Trim();

    // 2. Replace multiple spaces with single space
    input = Regex.Replace(input, @"\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(100));

    // 3. Remove non-breaking spaces
    input = input.Replace('\u00A0', ' ');

    return input;
}
```

**Null Handling Strategies:**
- **Drop Nulls**: Remove rows with nulls
- **Forward Fill**: Use previous non-null value
- **Backward Fill**: Use next non-null value
- **Mean Fill**: Replace with column mean
- **Mode Fill**: Replace with most common value
- **Sentinel**: Replace with sentinel value (e.g., -1, "N/A")

### 4. Duplicate Detection Strategies (9 strategies)

| Strategy ID | Algorithm | Description |
|------------|-----------|-------------|
| `duplicate-exact` | **Exact Match** | SHA-256 hash matching |
| `duplicate-fuzzy` | **Levenshtein Distance** | Edit distance ≤ threshold |
| `duplicate-soundex` | **Phonetic** | Sound-alike matching |
| `duplicate-metaphone` | **Double Metaphone** | Phonetic encoding |
| `duplicate-jaccard` | **Set Similarity** | Jaccard coefficient |
| `duplicate-cosine` | **Vector Similarity** | Cosine similarity (TF-IDF) |
| `duplicate-embedding` | **Neural** | Semantic embedding similarity |
| `duplicate-blocking` | **Bucketing** | Reduce comparisons via blocking |
| `duplicate-probabilistic` | **Fellegi-Sunter** | Probabilistic record linkage |

**Levenshtein Distance:**
- **Algorithm**: Dynamic programming O(nm)
- **Threshold**: Distance ≤ 2 for fuzzy match (configurable)
- **Example**: `"John Smith"` vs `"Jon Smith"` → distance = 1 (fuzzy match)
- **Use Case**: Detect typos, misspellings

**Soundex:**
- **Algorithm**: Phonetic encoding to 4-character code
- **Example**: `"Smith"` → `S530`, `"Smythe"` → `S530` (match)
- **Use Case**: Find names that sound alike
- **Limitation**: English-focused, poor for non-European names

**Combined Fuzzy Matching:**
```csharp
bool IsDuplicate(string a, string b)
{
    // Multi-criteria fuzzy match
    int levDistance = LevenshteinDistance(a, b);
    string soundexA = Soundex(a);
    string soundexB = Soundex(b);

    return (levDistance <= 2) || (soundexA == soundexB);
}
```

### 5. Standardization Strategies (7 strategies)

| Strategy ID | Domain | Description |
|------------|--------|-------------|
| `standardization-address` | **Address** | USPS address normalization |
| `standardization-phone` | **Phone** | E.164 international format |
| `standardization-email` | **Email** | Lowercase + normalization |
| `standardization-name` | **Names** | Title case, remove honorifics |
| `standardization-date` | **Dates** | ISO 8601 format |
| `standardization-currency` | **Currency** | ISO 4217 codes |
| `standardization-country` | **Country** | ISO 3166-1 alpha-2 codes |

**Address Standardization:**
- **Input**: `"123 main st apt 4b new york ny 10001"`
- **Output**: `"123 Main Street, Apartment 4B, New York, NY 10001-1234"`
- **Components**: Street number, street name, unit, city, state, ZIP+4

**Phone Standardization:**
- **Input**: `"(555) 123-4567"`, `"555.123.4567"`, `"5551234567"`
- **Output**: `"+15551234567"` (E.164 format)
- **International**: `"+44 20 7946 0958"` → `"+442079460958"`

### 6. Scoring Strategies (8 strategies)

| Strategy ID | Dimension | Description |
|------------|-----------|-------------|
| `scoring-completeness` | **Null %** | Percentage of non-null values |
| `scoring-accuracy` | **Correctness** | Validation pass rate |
| `scoring-consistency` | **Uniformity** | Consistent formatting |
| `scoring-timeliness` | **Freshness** | Data age vs SLA |
| `scoring-uniqueness` | **Dedup** | Percentage unique records |
| `scoring-validity` | **Constraints** | Constraint satisfaction rate |
| `scoring-composite` | **Weighted** | Weighted average of dimensions |
| `scoring-ml` | **AI-Powered** | ML-based quality prediction |

**Weighted Dimension Scoring:**
```json
{
  "QualityScoreWeights": {
    "Completeness": 0.25,
    "Accuracy": 0.30,
    "Consistency": 0.20,
    "Timeliness": 0.15,
    "Uniqueness": 0.10
  }
}
```

**Score Calculation:**
```csharp
double CompositeQualityScore(DatasetMetrics metrics)
{
    double completeness = 1.0 - (metrics.NullCount / (double)metrics.TotalRows);
    double accuracy = metrics.ValidationPassCount / (double)metrics.TotalRows;
    double consistency = metrics.ConsistentFormatCount / (double)metrics.TotalRows;
    double timeliness = metrics.DataAge < metrics.SlaHours ? 1.0 : 0.0;
    double uniqueness = metrics.UniqueCount / (double)metrics.TotalRows;

    return (completeness * 0.25) +
           (accuracy * 0.30) +
           (consistency * 0.20) +
           (timeliness * 0.15) +
           (uniqueness * 0.10);
}
```

### 7. Monitoring Strategies (5 strategies)

| Strategy ID | Purpose | Description |
|------------|---------|-------------|
| `monitoring-real-time` | **Live Metrics** | Real-time quality metrics |
| `monitoring-drift-detection` | **Anomaly** | Detect distribution drift |
| `monitoring-threshold-alerts` | **Alerts** | Alert on quality threshold violations |
| `monitoring-trend-analysis` | **Trends** | Long-term quality trends |
| `monitoring-sla-tracking` | **SLA** | Track quality SLA compliance |

**Drift Detection (Kolmogorov-Smirnov Test):**
- **Purpose**: Detect if data distribution changed
- **Algorithm**: Two-sample K-S test (p-value < 0.05 = drift detected)
- **Example**: If mean salary shifts from $50K to $80K, detect distribution change
- **Action**: Alert data engineers, trigger re-profiling

### 8. Reporting Strategies (3 strategies)

| Strategy ID | Report Type | Description |
|------------|-------------|-------------|
| `reporting-dashboard` | **Visual** | Quality score dashboards |
| `reporting-detailed` | **Detailed** | Row-level quality issues |
| `reporting-executive-summary` | **Summary** | High-level quality KPIs |

### 9. Predictive Quality Strategies (2 strategies)

| Strategy ID | Prediction | Description |
|------------|------------|-------------|
| `predictive-quality-forecast` | **Forecast** | Predict future quality scores |
| `predictive-issue-detection` | **Anomaly** | Predict quality issues before they occur |

## Quality Dimensions

### 1. Completeness
- **Definition**: Percentage of non-null values
- **Formula**: `(TotalValues - NullCount) / TotalValues`
- **Target**: ≥95% completeness

### 2. Accuracy
- **Definition**: Percentage of values passing validation
- **Formula**: `ValidationPassCount / TotalValues`
- **Target**: ≥98% accuracy

### 3. Consistency
- **Definition**: Format uniformity across records
- **Example**: All dates in same format, all phone numbers in E.164
- **Formula**: `ConsistentFormatCount / TotalValues`
- **Target**: ≥99% consistency

### 4. Timeliness
- **Definition**: Data freshness relative to SLA
- **Formula**: `DataAge < SlaHours ? 1.0 : 0.0`
- **Target**: 100% within SLA

### 5. Uniqueness
- **Definition**: Percentage of unique records (no duplicates)
- **Formula**: `UniqueCount / TotalCount`
- **Target**: ≥99% uniqueness

## Message Bus Topics

### Publishes
- `quality.score.updated` — Quality score computed for dataset
- `quality.validation.failed` — Validation failures detected
- `quality.drift.detected` — Distribution drift detected
- `quality.sla.violated` — Quality SLA violated

### Subscribes
None — self-contained quality analysis

## Configuration

```json
{
  "UltimateDataQuality": {
    "Validation": {
      "RegexTimeoutMs": 100,
      "EnableFuzzyMatching": true,
      "LevenshteinThreshold": 2
    },
    "Profiling": {
      "UseStreamingAlgorithms": true,
      "CardinalityEstimator": "HyperLogLog",
      "PercentileAlgorithm": "P2"
    },
    "Scoring": {
      "CompletenessWeight": 0.25,
      "AccuracyWeight": 0.30,
      "ConsistencyWeight": 0.20,
      "TimelinessWeight": 0.15,
      "UniquenessWeight": 0.10
    },
    "DuplicateDetection": {
      "Algorithm": "Levenshtein+Soundex",
      "MaxComparisons": 1000000
    }
  }
}
```

## Performance Characteristics

| Operation | Throughput | Space Complexity |
|-----------|------------|------------------|
| Regex Validation | 100K rows/sec | O(1) |
| Statistical Profiling | 500K rows/sec | O(1) streaming |
| Levenshtein Distance | 10K comparisons/sec | O(nm) |
| Cardinality (HyperLogLog) | 1M inserts/sec | O(log log n) ≈ 1KB |
| Quality Score | 200K rows/sec | O(1) |

## Dependencies

### Required Plugins
None — fully self-contained

### Optional Plugins
- **UltimateDataLake** — Can consume quality scores for zone promotion decisions
- **UltimateDataGovernance** — Can enforce quality policies

## Compliance & Standards

- **ISO 8000**: Data quality standard
- **DAMA-DMBOK**: Data quality framework
- **DQM Framework**: Six dimensions of data quality

## Production Readiness

- **Status:** Production-Ready ✓
- **Test Coverage:** Unit tests for all 63 strategies
- **Real Implementations:** Statistical profiling (Welford's, P², HyperLogLog), Levenshtein distance, Soundex
- **Documentation:** Complete quality API documentation
- **Performance:** Benchmarked at 500K rows/sec profiling
- **Streaming**: Constant-space algorithms for large datasets
- **Audit:** All quality checks logged

## Use Cases

### 1. Data Lake Zone Promotion
- **Scenario**: Promote data from Bronze → Silver only if quality ≥ 0.95
- **Implementation**: Quality gate in UltimateDataLake consumes quality scores
- **Benefit**: Prevent low-quality data from entering curated zones

### 2. ML Feature Store Validation
- **Scenario**: Validate ML features before model training
- **Implementation**: Run validation rules on feature data
- **Benefit**: Prevent model degradation from bad features

### 3. Regulatory Compliance
- **Scenario**: Ensure data quality meets regulatory standards
- **Implementation**: Monitor completeness, accuracy metrics
- **Benefit**: Avoid compliance violations

## Version History

### v3.0.0 (2026-02-16)
- Initial production release
- 63 strategies across 9 categories
- Streaming statistical profiling (Welford's, P², HyperLogLog)
- Levenshtein + Soundex duplicate detection
- Weighted dimension quality scoring
- Real-time monitoring and drift detection
- GDPR/CCPA PII detection
- Production-ready algorithms

---

**Last Updated:** 2026-02-16
**Maintained By:** DataWarehouse Platform Team

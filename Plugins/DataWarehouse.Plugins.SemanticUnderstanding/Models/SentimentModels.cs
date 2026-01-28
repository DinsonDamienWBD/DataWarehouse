namespace DataWarehouse.Plugins.SemanticUnderstanding.Models
{
    /// <summary>
    /// Result of sentiment analysis.
    /// </summary>
    public sealed record SentimentAnalysisResult
    {
        /// <summary>Overall sentiment classification.</summary>
        public SentimentClass OverallSentiment { get; init; }

        /// <summary>Overall sentiment score (-1 to 1).</summary>
        public float OverallScore { get; init; }

        /// <summary>Confidence in overall sentiment (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>Detected emotions.</summary>
        public List<DetectedEmotion> Emotions { get; init; } = new();

        /// <summary>Aspect-based sentiments (sentiment for specific topics/aspects).</summary>
        public List<AspectSentiment> AspectSentiments { get; init; } = new();

        /// <summary>Sentence-level sentiments.</summary>
        public List<SentenceSentiment> SentenceSentiments { get; init; } = new();

        /// <summary>Whether AI was used for analysis.</summary>
        public bool UsedAI { get; init; }

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Text length analyzed.</summary>
        public int TextLength { get; init; }
    }

    /// <summary>
    /// Sentiment classification.
    /// </summary>
    public enum SentimentClass
    {
        /// <summary>Strongly positive sentiment.</summary>
        VeryPositive = 2,
        /// <summary>Positive sentiment.</summary>
        Positive = 1,
        /// <summary>Neutral sentiment.</summary>
        Neutral = 0,
        /// <summary>Negative sentiment.</summary>
        Negative = -1,
        /// <summary>Strongly negative sentiment.</summary>
        VeryNegative = -2,
        /// <summary>Mixed positive and negative.</summary>
        Mixed = 3
    }

    /// <summary>
    /// A detected emotion in text.
    /// </summary>
    public sealed class DetectedEmotion
    {
        /// <summary>Emotion type.</summary>
        public EmotionType Emotion { get; init; }

        /// <summary>Intensity score (0-1).</summary>
        public float Intensity { get; init; }

        /// <summary>Confidence in detection (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>Text spans showing this emotion.</summary>
        public List<string> EvidenceSpans { get; init; } = new();
    }

    /// <summary>
    /// Types of emotions.
    /// </summary>
    public enum EmotionType
    {
        /// <summary>Joy, happiness.</summary>
        Joy,
        /// <summary>Sadness, grief.</summary>
        Sadness,
        /// <summary>Anger, frustration.</summary>
        Anger,
        /// <summary>Fear, anxiety.</summary>
        Fear,
        /// <summary>Surprise, amazement.</summary>
        Surprise,
        /// <summary>Disgust, contempt.</summary>
        Disgust,
        /// <summary>Trust, confidence.</summary>
        Trust,
        /// <summary>Anticipation, expectation.</summary>
        Anticipation
    }

    /// <summary>
    /// Sentiment for a specific aspect/topic.
    /// </summary>
    public sealed class AspectSentiment
    {
        /// <summary>The aspect/topic being evaluated.</summary>
        public required string Aspect { get; init; }

        /// <summary>Sentiment class for this aspect.</summary>
        public SentimentClass Sentiment { get; init; }

        /// <summary>Sentiment score (-1 to 1).</summary>
        public float Score { get; init; }

        /// <summary>Confidence (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>Text spans mentioning this aspect.</summary>
        public List<string> Mentions { get; init; } = new();

        /// <summary>Opinion words/phrases associated with this aspect.</summary>
        public List<string> OpinionTerms { get; init; } = new();
    }

    /// <summary>
    /// Sentence-level sentiment.
    /// </summary>
    public sealed class SentenceSentiment
    {
        /// <summary>The sentence text.</summary>
        public required string Text { get; init; }

        /// <summary>Start position in original text.</summary>
        public int StartPosition { get; init; }

        /// <summary>Sentiment class.</summary>
        public SentimentClass Sentiment { get; init; }

        /// <summary>Sentiment score (-1 to 1).</summary>
        public float Score { get; init; }

        /// <summary>Confidence (0-1).</summary>
        public float Confidence { get; init; }
    }

    /// <summary>
    /// Sentiment trend over time.
    /// </summary>
    public sealed class SentimentTrend
    {
        /// <summary>Time period for the trend.</summary>
        public DateTimeOffset StartTime { get; init; }

        /// <summary>End time of the period.</summary>
        public DateTimeOffset EndTime { get; init; }

        /// <summary>Average sentiment score for this period.</summary>
        public float AverageScore { get; init; }

        /// <summary>Sentiment distribution.</summary>
        public Dictionary<SentimentClass, int> Distribution { get; init; } = new();

        /// <summary>Dominant emotion in this period.</summary>
        public EmotionType? DominantEmotion { get; init; }

        /// <summary>Number of documents analyzed.</summary>
        public int DocumentCount { get; init; }

        /// <summary>Trend direction compared to previous period.</summary>
        public TrendDirection Trend { get; init; }

        /// <summary>Significant topics in this period.</summary>
        public List<string> SignificantTopics { get; init; } = new();
    }

    /// <summary>
    /// Trend direction.
    /// </summary>
    public enum TrendDirection
    {
        /// <summary>Improving sentiment.</summary>
        Improving,
        /// <summary>Stable sentiment.</summary>
        Stable,
        /// <summary>Declining sentiment.</summary>
        Declining
    }

    /// <summary>
    /// Configuration for sentiment analysis.
    /// </summary>
    public sealed class SentimentAnalysisConfig
    {
        /// <summary>Enable sentence-level analysis.</summary>
        public bool EnableSentenceLevel { get; init; } = true;

        /// <summary>Enable aspect-based analysis.</summary>
        public bool EnableAspectBased { get; init; } = true;

        /// <summary>Enable emotion detection.</summary>
        public bool EnableEmotionDetection { get; init; } = true;

        /// <summary>Aspects to focus on (empty = auto-detect).</summary>
        public List<string> FocusAspects { get; init; } = new();

        /// <summary>Prefer AI over statistical methods.</summary>
        public bool PreferAI { get; init; } = true;

        /// <summary>Minimum text length for reliable analysis.</summary>
        public int MinTextLength { get; init; } = 10;

        /// <summary>Maximum sentences to analyze per document.</summary>
        public int MaxSentences { get; init; } = 1000;
    }
}

/// <summary>
/// Keeps the latest quiz score while changing from the quiz scene to the result scene.
/// </summary>
public static class QuizSessionResult
{
    public static int CorrectCount { get; private set; }
    public static int TotalCount { get; private set; }

    public static void SetScore(int correctCount, int totalCount)
    {
        CorrectCount = correctCount;
        TotalCount = totalCount;
    }
}

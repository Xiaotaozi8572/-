using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Displays one of several result-card sprites according to the latest quiz score.
/// </summary>
public sealed class QuizResultSceneController : MonoBehaviour
{
    [Header("Result display")]
    [SerializeField] private Image resultCardImage;
    [Tooltip("Index 0 is the 0-correct card, index 8 is the 8-correct card.")]
    [SerializeField] private Sprite[] resultCards;
    [SerializeField] private TMP_Text scoreText;
    [Min(1)] [SerializeField] private int fallbackTotalQuestions = 8;

    [Header("Scene navigation")]
    [SerializeField] private string quizSceneName = "08_答题页面";
    [SerializeField] private string returnSceneName = "07_虚拟课堂";

    private void Start()
    {
        int total = QuizSessionResult.TotalCount > 0
            ? QuizSessionResult.TotalCount
            : fallbackTotalQuestions;
        int correct = Mathf.Clamp(QuizSessionResult.CorrectCount, 0, total);

        if (resultCardImage != null && resultCards != null && resultCards.Length > 0)
        {
            int cardIndex = Mathf.Clamp(correct, 0, resultCards.Length - 1);
            if (resultCards[cardIndex] != null)
            {
                resultCardImage.sprite = resultCards[cardIndex];
                resultCardImage.preserveAspect = true;
            }
        }

        if (scoreText != null)
            scoreText.text = $"您的最终得分：{correct}/{total}";
    }

    public void RestartQuiz()
    {
        SceneManager.LoadScene(quizSceneName);
    }

    public void ReturnToClassroom()
    {
        SceneManager.LoadScene(returnSceneName);
    }
}

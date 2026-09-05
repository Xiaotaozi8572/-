using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public sealed class QuizQuestion
{
    [TextArea(2, 5)] public string question;
    [Tooltip("Optional question image. When assigned, it is used instead of Question text.")]
    public Sprite questionImage;
    public string[] options;
    [Tooltip("Optional image answers. When assigned, these are used instead of option text.")]
    public Sprite[] optionImages;
    [Min(0)] public int correctOptionIndex;
}

/// <summary>
/// Reuses one question card for multiple-choice and true/false questions.
/// </summary>
public sealed class QuizController : MonoBehaviour
{
    [Header("Question bank")]
    [SerializeField] private QuizQuestion[] questions;
    [SerializeField] private bool randomizeQuestionOrder;
    [Tooltip("0 means use every question in the bank.")]
    [Min(0)] [SerializeField] private int questionsPerQuiz;

    [Header("Question page")]
    [SerializeField] private GameObject questionPage;
    [SerializeField] private TMP_Text progressText;
    [SerializeField] private TMP_Text questionTypeText;
    [SerializeField] private TMP_Text questionText;
    [SerializeField] private Image questionContentImage;
    [SerializeField] private Button[] optionButtons;
    [SerializeField] private TMP_Text[] optionTexts;
    [SerializeField] private Image[] optionContentImages;
    [SerializeField] private Image[] optionBackgrounds;
    [SerializeField] private TMP_Text feedbackText;
    [SerializeField] private Button nextButton;
    [Header("Answer flow")]
    [SerializeField] private bool autoAdvanceAfterAnswer = true;
    [Min(0.1f)] [SerializeField] private float autoAdvanceDelay = 1f;

    [Header("Result page")]
    [SerializeField] private GameObject resultPage;
    [SerializeField] private Image resultCardImage;
    [Tooltip("Index 0 is the 0-correct card, index 8 is the 8-correct card.")]
    [SerializeField] private Sprite[] resultCards;
    [SerializeField] private TMP_Text scoreText;
    [SerializeField] private TMP_Text resultCommentText;

    [Header("Colors")]
    [SerializeField] private Color normalColor = new Color(0.72f, 0.72f, 0.72f, 0.55f);
    [SerializeField] private Color correctColor = new Color(0.15f, 0.8f, 0.45f, 0.9f);
    [SerializeField] private Color wrongColor = new Color(0.95f, 0.3f, 0.3f, 0.9f);

    private int currentQuestionIndex;
    private int correctCount;
    private int answeredCount;
    private bool questionAnswered;
    private readonly List<QuizQuestion> activeQuestions = new List<QuizQuestion>();

    public int CorrectCount => correctCount;
    public int WrongCount => answeredCount - correctCount;

    private void Awake()
    {
        for (int i = 0; i < optionButtons.Length; i++)
        {
            int optionIndex = i;
            if (optionButtons[i] != null)
                optionButtons[i].onClick.AddListener(() => SelectOption(optionIndex));
        }

        if (nextButton != null)
            nextButton.onClick.AddListener(NextQuestion);

        StartQuiz();
    }

    public void StartQuiz()
    {
        StopAllCoroutines();
        currentQuestionIndex = 0;
        correctCount = 0;
        answeredCount = 0;
        BuildActiveQuestionList();

        if (resultPage != null)
            resultPage.SetActive(false);
        if (questionPage != null)
            questionPage.SetActive(true);

        ShowCurrentQuestion();
    }

    public void SelectOption(int optionIndex)
    {
        if (questionAnswered || activeQuestions.Count == 0)
            return;

        QuizQuestion current = activeQuestions[currentQuestionIndex];
        int optionCount = GetOptionCount(current);
        if (optionIndex < 0 || optionIndex >= optionCount)
            return;

        questionAnswered = true;
        answeredCount++;

        bool isCorrect = optionIndex == current.correctOptionIndex;
        if (isCorrect)
            correctCount++;

        for (int i = 0; i < optionButtons.Length; i++)
        {
            if (optionButtons[i] != null)
            {
                Color resultColor = i == current.correctOptionIndex ? correctColor :
                    i == optionIndex && !isCorrect ? wrongColor : normalColor;
                ColorBlock colors = optionButtons[i].colors;
                colors.disabledColor = resultColor;
                optionButtons[i].colors = colors;
                optionButtons[i].interactable = false;
            }
        }

        SetOptionColor(current.correctOptionIndex, correctColor);
        if (!isCorrect)
            SetOptionColor(optionIndex, wrongColor);

        if (feedbackText != null)
            feedbackText.text = isCorrect ? "回答正确" : "回答错误";

        if (autoAdvanceAfterAnswer)
        {
            if (nextButton != null)
                nextButton.gameObject.SetActive(false);
            StartCoroutine(AdvanceAfterDelay());
        }
        else if (nextButton != null)
        {
            nextButton.gameObject.SetActive(true);
        }
    }

    public void NextQuestion()
    {
        if (!questionAnswered)
            return;

        currentQuestionIndex++;
        if (currentQuestionIndex >= activeQuestions.Count)
        {
            ShowResult();
            return;
        }

        ShowCurrentQuestion();
    }

    private void ShowCurrentQuestion()
    {
        if (activeQuestions.Count == 0)
        {
            if (questionText != null)
                questionText.text = "请先在 Quiz Controller 中添加题目。";
            return;
        }

        currentQuestionIndex = Mathf.Clamp(currentQuestionIndex, 0, activeQuestions.Count - 1);
        QuizQuestion current = activeQuestions[currentQuestionIndex];
        questionAnswered = false;

        if (progressText != null)
            progressText.text = $"当前进度：{currentQuestionIndex + 1} / {activeQuestions.Count}";
        if (questionTypeText != null)
            questionTypeText.text = GetOptionCount(current) == 2 ? "判断题" : "单选题";
        bool usesQuestionImage = current.questionImage != null;
        if (questionContentImage != null)
        {
            questionContentImage.gameObject.SetActive(usesQuestionImage);
            if (usesQuestionImage)
            {
                questionContentImage.sprite = current.questionImage;
                questionContentImage.preserveAspect = true;
            }
        }
        if (questionText != null)
        {
            questionText.gameObject.SetActive(!usesQuestionImage);
            if (!usesQuestionImage)
                questionText.text = $"{currentQuestionIndex + 1}. {current.question}";
        }
        if (feedbackText != null)
            feedbackText.text = string.Empty;
        if (nextButton != null)
            nextButton.gameObject.SetActive(false);

        for (int i = 0; i < optionButtons.Length; i++)
        {
            bool shouldShow = i < GetOptionCount(current);
            if (optionButtons[i] != null)
            {
                optionButtons[i].gameObject.SetActive(shouldShow);
                ColorBlock colors = optionButtons[i].colors;
                colors.disabledColor = normalColor;
                optionButtons[i].colors = colors;
                optionButtons[i].interactable = true;
            }

            bool usesImages = current.optionImages != null && current.optionImages.Length > 0;

            if (i < optionContentImages.Length && optionContentImages[i] != null)
            {
                optionContentImages[i].gameObject.SetActive(shouldShow && usesImages);
                if (shouldShow && usesImages)
                {
                    optionContentImages[i].sprite = current.optionImages[i];
                    optionContentImages[i].preserveAspect = true;
                }
            }

            if (i < optionTexts.Length && optionTexts[i] != null)
            {
                optionTexts[i].gameObject.SetActive(shouldShow && !usesImages);
                if (shouldShow && !usesImages && current.options != null && i < current.options.Length)
                    optionTexts[i].text = current.options[i];
            }

            SetOptionColor(i, normalColor);
        }
    }

    private void ShowResult()
    {
        int total = activeQuestions.Count;
        if (questionPage != null)
            questionPage.SetActive(false);
        if (resultPage != null)
            resultPage.SetActive(true);

        if (resultCardImage != null && resultCards != null && resultCards.Length > 0)
        {
            int cardIndex = Mathf.Clamp(correctCount, 0, resultCards.Length - 1);
            if (resultCards[cardIndex] != null)
            {
                resultCardImage.sprite = resultCards[cardIndex];
                resultCardImage.preserveAspect = true;
            }
        }

        if (scoreText != null)
            scoreText.text = $"答对 {correctCount} 题 / 共 {total} 题\n答错 {WrongCount} 题";

        if (resultCommentText != null)
        {
            float rate = total == 0 ? 0f : (float)correctCount / total;
            resultCommentText.text = rate >= 0.8f ? "掌握得很好！" :
                rate >= 0.6f ? "成绩不错，再复习一下重点吧。" : "建议返回课堂继续学习。";
        }
    }

    private void SetOptionColor(int index, Color color)
    {
        if (optionBackgrounds != null && index >= 0 && index < optionBackgrounds.Length &&
            optionBackgrounds[index] != null)
        {
            optionBackgrounds[index].color = color;
        }
    }

    private IEnumerator AdvanceAfterDelay()
    {
        yield return new WaitForSecondsRealtime(autoAdvanceDelay);
        NextQuestion();
    }

    private void BuildActiveQuestionList()
    {
        activeQuestions.Clear();
        if (questions == null)
            return;

        foreach (QuizQuestion question in questions)
        {
            if (question != null)
                activeQuestions.Add(question);
        }

        if (randomizeQuestionOrder)
        {
            for (int i = activeQuestions.Count - 1; i > 0; i--)
            {
                int swapIndex = UnityEngine.Random.Range(0, i + 1);
                (activeQuestions[i], activeQuestions[swapIndex]) =
                    (activeQuestions[swapIndex], activeQuestions[i]);
            }
        }

        if (questionsPerQuiz > 0 && activeQuestions.Count > questionsPerQuiz)
            activeQuestions.RemoveRange(questionsPerQuiz, activeQuestions.Count - questionsPerQuiz);
    }

    private static int GetOptionCount(QuizQuestion question)
    {
        if (question == null)
            return 0;
        if (question.optionImages != null && question.optionImages.Length > 0)
            return question.optionImages.Length;
        return question.options == null ? 0 : question.options.Length;
    }
}

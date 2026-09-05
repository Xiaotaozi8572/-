using UnityEngine;

public class CardSelector : MonoBehaviour
{
    public Transform[] cards;

    private int currentIndex = -1;

    public void SelectCard(int index)
    {
        currentIndex = index;

        for (int i = 0; i < cards.Length; i++)
        {
            if (i == currentIndex)
            {
                cards[i].localScale =
                    Vector3.one * 1.3f;
            }
            else
            {
                cards[i].localScale =
                    Vector3.one * 0.8f;
            }
        }
    }
}
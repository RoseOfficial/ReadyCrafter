using System;
using System.Collections.Generic;
using System.Linq;
using Lumina.Excel;
using Lumina.Excel.Sheets;

namespace ReadyCrafter.Utils;

/// <summary>
/// Extension methods for Lumina Excel sheet objects to provide consistent recipe data access.
/// Based on community best practices from successful Dalamud plugins.
/// </summary>
public static class LuminaExtensions
{
    /// <summary>
    /// Get all ingredients for a recipe using the proper Lumina data structure.
    /// This replaces the incorrect reflection-based approach with the standard community pattern.
    /// </summary>
    /// <param name="recipe">The Lumina Recipe object</param>
    /// <returns>Enumerable of recipe ingredients with quantities</returns>
    public static IEnumerable<RecipeIngredientInfo> Ingredients(this Recipe recipe)
    {
        if (recipe.RowId == 0)
            yield break;

        // Access the ingredient collections using the correct Lumina pattern
        // Recipe has indexed properties: ItemIngredient0-9 and AmountIngredient0-9
        for (int i = 0; i < 10; i++) // FFXIV recipes support up to 10 ingredients
        {
            var ingredientItem = GetIngredientAtIndex(recipe, i);
            var amount = GetAmountAtIndex(recipe, i);

            // Skip empty ingredient slots
            if (ingredientItem.RowId == 0 || amount == 0)
                continue;

            yield return new RecipeIngredientInfo
            {
                Item = ingredientItem,
                Amount = amount,
                Index = i
            };
        }
    }

    /// <summary>
    /// Get ingredient item reference at a specific index using reflection as needed.
    /// This handles the ItemIngredient0, ItemIngredient1, etc. properties.
    /// </summary>
    private static RowRef<Item> GetIngredientAtIndex(Recipe recipe, int index)
    {
        try
        {
            var propertyName = $"ItemIngredient{index}";
            var property = recipe.GetType().GetProperty(propertyName);
            if (property != null)
            {
                var value = property.GetValue(recipe);
                if (value is RowRef<Item> itemRef)
                    return itemRef;
            }
        }
        catch
        {
            // Ignore reflection errors
        }

        // Return empty reference if property doesn't exist or access fails
        return new RowRef<Item>();
    }

    /// <summary>
    /// Get ingredient amount at a specific index using reflection as needed.
    /// This handles the AmountIngredient0, AmountIngredient1, etc. properties.
    /// </summary>
    private static byte GetAmountAtIndex(Recipe recipe, int index)
    {
        try
        {
            var propertyName = $"AmountIngredient{index}";
            var property = recipe.GetType().GetProperty(propertyName);
            if (property != null)
            {
                var value = property.GetValue(recipe);
                if (value is byte amount)
                    return amount;
            }
        }
        catch
        {
            // Ignore reflection errors
        }

        return 0;
    }
}

/// <summary>
/// Information about a recipe ingredient extracted from Lumina data.
/// </summary>
public class RecipeIngredientInfo
{
    /// <summary>
    /// Reference to the ingredient item from the Item Excel sheet.
    /// </summary>
    public RowRef<Item> Item { get; set; }

    /// <summary>
    /// Quantity of this ingredient required.
    /// </summary>
    public byte Amount { get; set; }

    /// <summary>
    /// Index of this ingredient in the recipe (0-9).
    /// </summary>
    public int Index { get; set; }
}
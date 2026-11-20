// Optional "unlock all cards" mode: builds a card box containing every playable card at max level,
// max skill level and max PERFECT/GREAT/drop stats. Controlled by offline\config.json.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

public static class AllCards
{
    public static object[] Cards;          // replacement UserHasCardData (null = feature off)
    public static object[] Dictionary;     // replacement CardDictionaryData
    public static int Count;
    public static List<Dictionary<string, object>> Pool;   // all deck-usable cards, for the gacha

    public static void LoadPool(string cdnFilesDir)
    {
        Pool = new List<Dictionary<string, object>>();
        foreach (object o in Table(cdnFilesDir, "M_card"))
        {
            Dictionary<string, object> c = (Dictionary<string, object>)o;
            if (I(c, "card_type_id") == 1 && I(c, "rarity_id") > 0) Pool.Add(c);
        }
        OfflineServer.Log("GACHA card pool: " + Pool.Count + " cards");
    }

    static JavaScriptSerializer Json()
    {
        JavaScriptSerializer js = new JavaScriptSerializer(); js.MaxJsonLength = int.MaxValue; js.RecursionLimit = 1000; return js;
    }

    public static object[] Table(string cdnFilesDir, string name)
    {
        string[] hits = Directory.GetFiles(cdnFilesDir, name + ".json", SearchOption.AllDirectories);
        if (hits.Length == 0) throw new Exception(name + ".json not found");
        return (object[])Json().DeserializeObject(OfflineServer.ReadTable(name, hits[0]));
    }

    static int I(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : -1; }

    public static void Build(string cdnFilesDir, string minRarity, object[] ownedCards)
    {
        object[] cards = Table(cdnFilesDir, "M_card");
        Dictionary<int, int> levelType = new Dictionary<int, int>();
        foreach (object o in Table(cdnFilesDir, "M_card_status")) { Dictionary<string, object> d = (Dictionary<string, object>)o; levelType[I(d, "id")] = I(d, "card_level_type_id"); }
        // level type -> (max level, total exp needed for it)
        Dictionary<int, long[]> maxLevel = new Dictionary<int, long[]>();
        foreach (object o in Table(cdnFilesDir, "M_card_level"))
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            int t = I(d, "card_level_type_id"), lv = I(d, "level");
            long[] cur;
            if (!maxLevel.TryGetValue(t, out cur) || lv > cur[0]) maxLevel[t] = new long[] { lv, Convert.ToInt64(d["total_exp_start"]) };
        }
        Dictionary<int, int> raritySort = new Dictionary<int, int>(); int minSort = 0;
        foreach (object o in Table(cdnFilesDir, "M_rarity"))
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            raritySort[I(d, "id")] = I(d, "sort_order");
            if (string.Equals(Convert.ToString(d["name"]), minRarity, StringComparison.OrdinalIgnoreCase)) minSort = I(d, "sort_order");
        }
        // max skill level per skill_id, from M_skill_param (falls back to 1 if it can't be worked out)
        Dictionary<int, int> maxSkill = new Dictionary<int, int>();
        try
        {
            foreach (object o in Table(cdnFilesDir, "M_skill_param"))
            {
                Dictionary<string, object> d = (Dictionary<string, object>)o;
                int sid = I(d, "skill_id"), lv = d.ContainsKey("skill_level") ? I(d, "skill_level") : I(d, "level");
                if (sid < 0 || lv < 0) continue;
                int cur; if (!maxSkill.TryGetValue(sid, out cur) || lv > cur) maxSkill[sid] = lv;
            }
        }
        catch (Exception ex) { OfflineServer.Log("CARDS could not read M_skill_param (" + ex.Message + "), skill levels stay at 1"); }

        // keep the user_has_card_id of cards already owned so existing decks stay valid
        Dictionary<string, string> ownedId = new Dictionary<string, string>();
        foreach (object o in ownedCards)
        {
            Dictionary<string, object> d = (Dictionary<string, object>)o;
            string cid = Convert.ToString(d["card_id"]);
            if (!ownedId.ContainsKey(cid)) ownedId[cid] = Convert.ToString(d["user_has_card_id"]);
        }

        List<object> list = new List<object>(), dict = new List<object>();
        foreach (object o in cards)
        {
            Dictionary<string, object> c = (Dictionary<string, object>)o;
            int id = I(c, "id"), rarity = I(c, "rarity_id");
            if (I(c, "card_type_id") != 1 || rarity < 0) continue;                     // only normal (deck-usable) cards
            int sort; if (!raritySort.TryGetValue(rarity, out sort) || sort < minSort) continue;
            int lt; long[] ml;
            long level = 1, exp = 0;
            if (levelType.TryGetValue(id, out lt) && maxLevel.TryGetValue(lt, out ml)) { level = ml[0]; exp = ml[1]; }
            int sk; if (!maxSkill.TryGetValue(I(c, "skill_id"), out sk) || sk < 1) sk = 1;
            string uid; if (!ownedId.TryGetValue(id.ToString(), out uid)) uid = (1000000 + id).ToString();

            Dictionary<string, object> e = new Dictionary<string, object>();
            e["card_id"] = id.ToString();
            e["level"] = level;
            e["total_exp"] = exp;
            e["skill_level"] = sk;
            e["hit_perfect_num"] = I(c, "max_hit_perfect_num");
            e["hit_great_num"] = I(c, "max_hit_great_num");
            e["drop_ratio"] = Convert.ToDecimal(c["max_drop_ratio"], CultureInfo.InvariantCulture);
            e["is_protected"] = "true";
            e["is_new"] = "false";
            e["user_has_card_id"] = uid;
            list.Add(e);
            Dictionary<string, object> de = new Dictionary<string, object>();
            de["card_id"] = id.ToString(); de["is_new"] = "false";
            dict.Add(de);
        }
        Cards = list.ToArray(); Dictionary = dict.ToArray(); Count = list.Count;
        OfflineServer.Log("CARDS unlock-all mode: " + Count + " cards at max level (min rarity " + (minSort > 0 ? minRarity : "any") + ", max skill levels known for " + maxSkill.Count + " skills)");
    }
}

namespace UrukCardEditor.Core;

public static class AbilityTemplates
{
    private const string DeathDamageMarker = "CARD_QUERY_CHOOSE_DEAL_1_DAMAGE";
    private const string DeathMinusCounterMarker = "CARD_QUERY_CHOOSE_CREATURE_TO_GET_MINUS1_MINUS1_COUNTER";

    public static string AddDeathDamage(CardSourceFormat format, string current)
    {
        if (current.Contains(DeathDamageMarker, StringComparison.Ordinal))
            return current;
        return format == CardSourceFormat.Xml
            ? AppendXml(current, DeathDamageXml)
            : LuaTableEditor.AppendTableItem(current, DeathDamageLua);
    }

    public static string AddDeathMinusCounter(CardSourceFormat format, string current)
    {
        if (current.Contains(DeathMinusCounterMarker, StringComparison.Ordinal))
            return current;
        return format == CardSourceFormat.Xml
            ? AppendXml(current, DeathMinusCounterXml)
            : LuaTableEditor.AppendTableItem(current, DeathMinusCounterLua);
    }

    public static string RemoveDuplicateKnownTriggers(CardSourceFormat format, string current, out int removedCount)
    {
        if (format == CardSourceFormat.Lua)
        {
            var withoutDamageDuplicates = LuaTableEditor.RemoveDuplicateTableItems(
                current, DeathDamageMarker, out var removedDamage);
            var result = LuaTableEditor.RemoveDuplicateTableItems(
                withoutDamageDuplicates, DeathMinusCounterMarker, out var removedCounters);
            removedCount = removedDamage + removedCounters;
            return result;
        }

        var wrapper = System.Xml.Linq.XDocument.Parse($"<ROOT>{current}</ROOT>", System.Xml.Linq.LoadOptions.PreserveWhitespace);
        removedCount = RemoveDuplicateXmlElements(wrapper.Root!, DeathDamageMarker) +
                       RemoveDuplicateXmlElements(wrapper.Root!, DeathMinusCounterMarker);
        return string.Join(Environment.NewLine, wrapper.Root!.Elements().Select(element => element.ToString()));
    }

    private static int RemoveDuplicateXmlElements(System.Xml.Linq.XElement root, string marker)
    {
        var matches = root.Elements().Where(element => element.ToString().Contains(marker, StringComparison.Ordinal)).ToList();
        foreach (var duplicate in matches.Skip(1))
            duplicate.Remove();
        return Math.Max(0, matches.Count - 1);
    }

    private static string AppendXml(string current, string element) =>
        string.IsNullOrWhiteSpace(current) ? element.Trim() : current.TrimEnd() + Environment.NewLine + element.Trim();

    private const string DeathDamageLua = """
{
  type = "triggered",
  {
    priority = "70",
    text = {
      { language_code = "en-US", data = T("When this creature dies, you may have it deal 1 damage to target creature or player.") },
      { language_code = "ru-RU", data = T("Когда это существо умирает, вы можете заставить его нанести 1 повреждение целевому существу или игроку.") }
    },
    mays = { {} },
    targets = {
      { tag = "CARD_QUERY_CHOOSE_DEAL_1_DAMAGE", definition = "0", compartment = "0", count = "1" }
    },
    target_definitions = {
      {
        id = "0",
        code = [[
          local filter = ClearFilter()
          filter:SetFilterType( FILTER_TYPE_CARDS + FILTER_TYPE_PLAYERS )
          filter:Set_Or()
          local creatures = filter:AddSubFilter_And()
          creatures:Add( FE_TYPE, OP_IS, CARD_TYPE_CREATURE )
          local walkers = filter:AddSubFilter_And()
          walkers:Add( FE_TYPE, OP_IS, CARD_TYPE_PLANESWALKER )
          walkers:Add( FE_TEAM, OP_NOT, EffectController():GetTeam() )
          local players = filter:AddSubFilter_And()
          players:SetFilterType( FILTER_TYPE_PLAYERS )
        ]]
      }
    },
    triggers = {
      { value = "ZONECHANGE_BEGIN", simple_qualifier = "self", to_zone = "ZONE_GRAVEYARD", from_zone = "ZONE_BATTLEFIELD" }
    },
    actions = {
      {
        type = "resolution_time",
        {
          code = [[
            local target_card = EffectDC():Get_Targets(0):Get_CardPtr(0)
            local target_player = EffectDC():Get_Targets(0):Get_PlayerPtr(0)
            if target_card ~= nil and target_card:GetCardType():Test( CARD_TYPE_CREATURE ) then
              EffectSourceLKI():DealDamageTo( 1, target_card )
            elseif target_player ~= nil then
              EffectSourceLKI():DealDamageTo( 1, target_player, REDIRECT_IMPLICIT_NO )
            elseif target_card ~= nil and target_card:GetCardType():Test( CARD_TYPE_PLANESWALKER ) then
              EffectSourceLKI():DealDamageTo( 1, target_card, target_card:GetPlayer(), REDIRECT_IMPLICIT_YES )
            end
          ]]
        }
      }
    }
  }
}
""";

    private const string DeathMinusCounterLua = """
{
  type = "triggered",
  {
    priority = "79",
    text = {
      { language_code = "en-US", data = T("When this creature dies, you may put a -1/-1 counter on target creature.") },
      { language_code = "ru-RU", data = T("Когда это существо умирает, вы можете положить один жетон -1/-1 на целевое существо.") }
    },
    mays = { {} },
    targets = {
      { tag = "CARD_QUERY_CHOOSE_CREATURE_TO_GET_MINUS1_MINUS1_COUNTER", definition = "0", compartment = "0", count = "1", up_to = "1" }
    },
    target_definitions = {
      {
        id = "0",
        code = [[
          local filter = ClearFilter()
          filter:Add( FE_TYPE, OP_IS, CARD_TYPE_CREATURE )
        ]]
      }
    },
    triggers = {
      { value = "ZONECHANGE_BEGIN", simple_qualifier = "self", to_zone = "ZONE_GRAVEYARD", from_zone = "ZONE_BATTLEFIELD" }
    },
    actions = {
      {
        type = "resolution_time",
        {
          code = [[
            local targetDC = EffectDC():Get_Targets(0)
            if targetDC == nil then return end
            local target = targetDC:Get_CardPtr(0)
            if target ~= nil then
              target:AddCounters( MTG():GetCountersType("-1/-1"), 1 )
            end
          ]]
        }
      }
    }
  }
}
""";

    private const string DeathDamageXml = """
<TRIGGERED_ABILITY priority="70">
  <LOCALISED_TEXT LanguageCode="en-US"><![CDATA[When this creature dies, you may have it deal 1 damage to target creature or player.]]></LOCALISED_TEXT>
  <LOCALISED_TEXT LanguageCode="ru-RU"><![CDATA[Когда это существо умирает, вы можете заставить его нанести 1 повреждение целевому существу или игроку.]]></LOCALISED_TEXT>
  <TRIGGER value="ZONECHANGE_BEGIN" simple_qualifier="self" to_zone="ZONE_GRAVEYARD" from_zone="ZONE_BATTLEFIELD" />
  <TARGET tag="CARD_QUERY_CHOOSE_DEAL_1_DAMAGE" definition="0" compartment="0" count="1" />
  <MAY />
  <TARGET_DEFINITION id="0"><![CDATA[
    local filter = ClearFilter()
    filter:SetFilterType( FILTER_TYPE_CARDS + FILTER_TYPE_PLAYERS )
    filter:Set_Or()
    local creatures = filter:AddSubFilter_And()
    creatures:Add( FE_TYPE, OP_IS, CARD_TYPE_CREATURE )
    local players = filter:AddSubFilter_And()
    players:SetFilterType( FILTER_TYPE_PLAYERS )
  ]]></TARGET_DEFINITION>
  <RESOLUTION_TIME_ACTION><![CDATA[
    local target_card = EffectDC():Get_Targets(0):Get_CardPtr(0)
    local target_player = EffectDC():Get_Targets(0):Get_PlayerPtr(0)
    if target_card ~= nil then
      EffectSourceLKI():DealDamageTo( 1, target_card )
    elseif target_player ~= nil then
      EffectSourceLKI():DealDamageTo( 1, target_player, REDIRECT_IMPLICIT_NO )
    end
  ]]></RESOLUTION_TIME_ACTION>
</TRIGGERED_ABILITY>
""";

    private const string DeathMinusCounterXml = """
<TRIGGERED_ABILITY priority="79">
  <LOCALISED_TEXT LanguageCode="en-US"><![CDATA[When this creature dies, you may put a -1/-1 counter on target creature.]]></LOCALISED_TEXT>
  <LOCALISED_TEXT LanguageCode="ru-RU"><![CDATA[Когда это существо умирает, вы можете положить один жетон -1/-1 на целевое существо.]]></LOCALISED_TEXT>
  <TRIGGER value="ZONECHANGE_BEGIN" simple_qualifier="self" to_zone="ZONE_GRAVEYARD" from_zone="ZONE_BATTLEFIELD" />
  <TARGET tag="CARD_QUERY_CHOOSE_CREATURE_TO_GET_MINUS1_MINUS1_COUNTER" definition="0" compartment="0" count="1" />
  <MAY />
  <TARGET_DEFINITION id="0"><![CDATA[
    local filter = ClearFilter()
    filter:Add( FE_TYPE, OP_IS, CARD_TYPE_CREATURE )
  ]]></TARGET_DEFINITION>
  <RESOLUTION_TIME_ACTION><![CDATA[
    local target = EffectDC():Get_Targets(0):Get_CardPtr(0)
    if target ~= nil then
      target:AddCounters( MTG():GetCountersType("-1/-1"), 1 )
    end
  ]]></RESOLUTION_TIME_ACTION>
</TRIGGERED_ABILITY>
""";
}

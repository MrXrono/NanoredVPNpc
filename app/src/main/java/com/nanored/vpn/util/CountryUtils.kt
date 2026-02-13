package com.nanored.vpn.util

object CountryUtils {

    private val countryNames = mapOf(
        "de" to "Германия",
        "ru" to "Россия",
        "us" to "США",
        "nl" to "Нидерланды",
        "gb" to "Великобритания",
        "uk" to "Великобритания",
        "fr" to "Франция",
        "jp" to "Япония",
        "sg" to "Сингапур",
        "hk" to "Гонконг",
        "kr" to "Южная Корея",
        "ca" to "Канада",
        "au" to "Австралия",
        "se" to "Швеция",
        "fi" to "Финляндия",
        "ch" to "Швейцария",
        "at" to "Австрия",
        "no" to "Норвегия",
        "dk" to "Дания",
        "pl" to "Польша",
        "it" to "Италия",
        "es" to "Испания",
        "pt" to "Португалия",
        "br" to "Бразилия",
        "in" to "Индия",
        "tr" to "Турция",
        "ua" to "Украина",
        "kz" to "Казахстан",
        "cz" to "Чехия",
        "ro" to "Румыния",
        "bg" to "Болгария",
        "ie" to "Ирландия",
        "il" to "Израиль",
        "ae" to "ОАЭ",
        "tw" to "Тайвань",
        "mx" to "Мексика",
        "ar" to "Аргентина",
        "za" to "ЮАР",
        "hu" to "Венгрия",
        "be" to "Бельгия",
        "lu" to "Люксембург",
        "lt" to "Литва",
        "lv" to "Латвия",
        "ee" to "Эстония",
        "md" to "Молдова",
        "ge" to "Грузия",
        "am" to "Армения",
        "az" to "Азербайджан",
        "by" to "Беларусь",
        "th" to "Таиланд",
        "vn" to "Вьетнам",
        "id" to "Индонезия",
        "my" to "Малайзия",
        "ph" to "Филиппины",
        "nz" to "Новая Зеландия",
        "cl" to "Чили",
        "co" to "Колумбия",
        "pe" to "Перу",
    )

    fun getCountryFlag(code: String): String {
        val upper = code.uppercase()
        if (upper.length != 2) return ""
        val first = 0x1F1E6 + (upper[0] - 'A')
        val second = 0x1F1E6 + (upper[1] - 'A')
        return String(Character.toChars(first)) + String(Character.toChars(second))
    }

    fun getCountryName(code: String): String {
        return countryNames[code.lowercase()] ?: code.uppercase()
    }

    fun extractCountryCode(remarks: String): String? {
        val trimmed = remarks.trim()
        if (trimmed.length < 2) return null
        val firstPart = trimmed.split(" ", "-", "_", ".").firstOrNull() ?: return null
        if (firstPart.length != 2) return null
        val code = firstPart.lowercase()
        if (code.all { it in 'a'..'z' }) return code
        return null
    }
}

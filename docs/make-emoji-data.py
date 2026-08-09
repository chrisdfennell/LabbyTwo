#!/usr/bin/env python3
"""Generate Core/emoji.tsv, the data behind the emoji picker.

Built from Python's own Unicode database rather than a downloaded table, so it needs no
network and no dependency — and regenerating it after a Python upgrade picks up whatever
emoji that Unicode version added.

    python docs/make-emoji-data.py

Output is one emoji per line: character, name, group. The app embeds it as a resource and
parses it once at startup.
"""

from __future__ import annotations

import sys
import unicodedata
from pathlib import Path

OUT = Path(__file__).resolve().parent.parent / "Core" / "emoji.tsv"

# The blocks that actually hold emoji. Alchemical symbols, box drawing and the rest of the
# pictographic planes are deliberately absent: they are not emoji and would render as
# unfamiliar glyphs in a picker.
BLOCKS = [
    (0x1F300, 0x1F5FF),   # Misc symbols and pictographs
    (0x1F600, 0x1F64F),   # Emoticons
    (0x1F680, 0x1F6FF),   # Transport and map
    (0x1F900, 0x1F9FF),   # Supplemental symbols and pictographs
    (0x1FA70, 0x1FAFF),   # Symbols and pictographs extended-A
    (0x2600, 0x26FF),     # Misc symbols
    (0x2700, 0x27BF),     # Dingbats
    (0x2B00, 0x2BFF),     # Misc symbols and arrows
    (0x2190, 0x21FF),     # Arrows
    (0x2300, 0x23FF),     # Misc technical — watches, hourglasses, media controls
    (0x25A0, 0x25FF),     # Geometric shapes
]

# Grouped by what the character is called, because Unicode names are descriptive and the
# blocks themselves are a jumble — faces, food and vehicles share one block. Order matters:
# the first group whose keywords match wins.
GROUPS: list[tuple[str, tuple[str, ...]]] = [
    ("Smileys", (
        "FACE", "SMIL", "GRIN", "TEAR", "SWEAT", "KISS", "HEART-EYES", "SCREAM", "ZANY",
        "MONKEY", "CLOWN", "OGRE", "GOBLIN", "GHOST", "ALIEN", "ROBOT", "SKULL", "POO",
    )),
    ("People", (
        "PERSON", "MAN", "WOMAN", "BOY", "GIRL", "BABY", "ADULT", "OLDER", "CHILD",
        "HAND", "FINGER", "THUMB", "FIST", "PALM", "NAIL", "ARM", "LEG", "FOOT", "EAR",
        "NOSE", "EYE", "TONGUE", "TOOTH", "BONE", "BRAIN", "LUNG", "HEART ", "BUST",
        "FOOTPRINTS", "SPEAKING", "DANCER", "PEOPLE", "FAMILY", "COUPLE", "NINJA",
        "MERMAID", "ELF", "FAIRY", "VAMPIRE", "ZOMBIE", "GENIE", "SUPERHERO", "SUPERVILLAIN",
        "RUNNER", "SURFER", "SWIMMER", "WEIGHT LIFTER", "GOLFER", "WRESTL", "CARTWHEEL",
        "JUGGL", "CLIMB", "ROWBOAT", "BATH", "HAIRCUT", "MASSAGE", "WALKING", "RUNNING",
        "KNEELING", "STANDING", "BOWING", "RAISING", "SHRUG", "POUTING", "FROWNING",
        "GESTUR", "TIPPING", "GUARD", "DETECTIVE", "PRINCE", "ASTRONAUT", "FIREFIGHTER",
        "PILOT", "JUDGE", "FARMER", "STUDENT", "TEACHER", "SINGER", "ARTIST", "SCIENTIST",
        "MECHANIC", "TECHNOLOGIST", "FACTORY WORKER", "OFFICE WORKER", "HEALTH WORKER",
        "PREGNANT", "BREAST", "SAUNA", "SKATER", "SNOWBOARDER", "SURFING", "BIKING",
        "MOUNTAIN BIK", "HORSE RACING", "SKIER", "FENCER", "HANDBALL", "WATER POLO",
    )),
    ("Animals & nature", (
        "DOG", "CAT", "MOUSE", "HAMSTER", "RABBIT", "FOX", "BEAR", "PANDA", "KOALA",
        "TIGER", "LION", "COW", "PIG", "FROG", "HORSE", "UNICORN", "ZEBRA", "DEER",
        "BISON", "OX", "BUFFALO", "GOAT", "SHEEP", "LLAMA", "GIRAFFE", "ELEPHANT",
        "MAMMOTH", "RHINO", "HIPPO", "CAMEL", "KANGAROO", "BADGER", "SLOTH", "OTTER",
        "SKUNK", "BEAVER", "BIRD", "CHICK", "HEN", "ROOSTER", "TURKEY", "DUCK", "SWAN",
        "OWL", "PEACOCK", "PARROT", "FLAMINGO", "DODO", "FEATHER", "GOOSE", "PENGUIN",
        "FROG", "CROCODILE", "TURTLE", "LIZARD", "SNAKE", "DRAGON", "DINOSAUR", "SAUROPOD",
        "WHALE", "DOLPHIN", "SEAL", "FISH", "SHARK", "OCTOPUS", "SHELL", "CORAL", "JELLYFISH",
        "SNAIL", "BUTTERFLY", "BUG", "ANT", "BEE", "BEETLE", "CRICKET", "COCKROACH",
        "SPIDER", "SCORPION", "MOSQUITO", "FLY", "WORM", "MICROBE", "CRAB", "LOBSTER",
        "SHRIMP", "SQUID", "OYSTER", "FLOWER", "BLOSSOM", "ROSE", "TULIP", "SUNFLOWER",
        "HIBISCUS", "BOUQUET", "SEEDLING", "TREE", "CACTUS", "HERB", "SHAMROCK", "CLOVER",
        "LEAF", "MAPLE", "MUSHROOM", "PLANT", "NEST", "SUN", "MOON", "STAR", "CLOUD",
        "RAIN", "SNOW", "TORNADO", "FOG", "WIND", "RAINBOW", "THERMOMETER", "DROPLET",
        "WAVE", "FIRE", "LIGHTNING", "COMET", "GLOBE", "EARTH", "VOLCANO", "MOUNTAIN",
        "RAT", "BAT", "BOAR", "PAW", "WOLF", "POODLE", "CHIPMUNK", "HEDGEHOG", "GORILLA",
        "ORANGUTAN", "CYCLONE", "TULIP", "PALM", "EVERGREEN", "DECIDUOUS", "BAMBOO",
        "PINE DECORATION", "FALLEN LEAF", "WILTED", "FOUR LEAF", "EAR OF", "LOTUS",
        "SUNRISE", "MILKY WAY", "GLOWING", "DIZZY", "HIGH VOLTAGE", "SNOWFLAKE", "SNOWMAN",
    )),
    ("Food & drink", (
        "APPLE", "PEAR", "ORANGE", "LEMON", "BANANA", "MELON", "GRAPE", "STRAWBERRY",
        "BERRY", "CHERR", "PEACH", "MANGO", "PINEAPPLE", "COCONUT", "KIWI", "TOMATO",
        "OLIVE", "AVOCADO", "AUBERGINE", "EGGPLANT", "POTATO", "CARROT", "MAIZE", "CORN",
        "PEPPER", "CUCUMBER", "LEAFY", "BROCCOLI", "GARLIC", "ONION", "PEANUT", "BEANS",
        "CHESTNUT", "BREAD", "CROISSANT", "BAGUETTE", "PRETZEL", "BAGEL", "PANCAKE",
        "WAFFLE", "CHEESE", "MEAT", "BACON", "BURGER", "FRIES", "PIZZA", "HOT DOG",
        "SANDWICH", "TACO", "BURRITO", "TAMALE", "FALAFEL", "EGG", "COOKING", "FONDUE",
        "POT OF", "SALAD", "POPCORN", "BUTTER", "SALT", "CANNED", "BENTO", "RICE",
        "CURRY", "RAMEN", "SPAGHETTI", "SWEET POTATO", "ODEN", "SUSHI", "PRAWN", "DUMPLING",
        "FORTUNE", "MOON CAKE", "OYSTER", "ICE CREAM", "SHAVED ICE", "DOUGHNUT", "COOKIE",
        "CAKE", "CUPCAKE", "PIE", "CHOCOLATE", "CANDY", "LOLLIPOP", "CUSTARD", "HONEY",
        "BOTTLE", "MILK", "COFFEE", "TEA", "SAKE", "CHAMPAGNE", "WINE", "COCKTAIL",
        "TROPICAL DRINK", "BEER", "CLINKING", "TUMBLER", "CUP WITH", "MATE", "ICE",
        "CHOPSTICKS", "FORK", "KNIFE", "SPOON", "PLATE", "JAR", "TEAPOT", "JUICE",
        "DANGO", "STEAMING", "SHALLOW PAN", "AMPHORA", "BAGUETTE", "FLATBREAD", "WAFFLE",
    )),
    ("Travel & places", (
        "CAR", "TAXI", "BUS", "TROLLEY", "TRUCK", "TRACTOR", "MOTOR", "SCOOTER",
        "BICYCLE", "SKATEBOARD", "ROLLER", "WHEEL", "TRAIN", "RAILWAY", "METRO", "TRAM",
        "MONORAIL", "STATION", "AEROPLANE", "AIRPLANE", "HELICOPTER", "ROCKET", "SATELLITE",
        "SHIP", "BOAT", "CANOE", "FERRY", "ANCHOR", "FUEL", "TRAFFIC", "CONSTRUCTION",
        "MAP", "COMPASS", "HOUSE", "HOME", "BUILDING", "OFFICE", "FACTORY", "HOSPITAL",
        "BANK", "HOTEL", "SCHOOL", "STORE", "CHURCH", "MOSQUE", "SYNAGOGUE", "TEMPLE",
        "KAABA", "SHRINE", "CASTLE", "STADIUM", "TENT", "BRIDGE", "FOUNTAIN", "STATUE",
        "TOWER", "SUNRISE", "SUNSET", "CITYSCAPE", "NIGHT WITH", "BEACH", "DESERT",
        "ISLAND", "PARK", "FERRIS", "ROLLERCOASTER", "CAROUSEL", "CIRCUS", "LUGGAGE",
        "SUITCASE", "PASSPORT", "TICKET", "SEAT", "BELLHOP", "MOAI", "HUT",
    )),
    ("Activities & objects", (
        "BALL", "TROPHY", "MEDAL", "AWARD", "GOAL", "SKI", "SNOWBOARD", "SLED",
        "CURLING", "FISHING", "DIVING", "BOXING", "MARTIAL", "KITE", "YO-YO", "GAME",
        "JOYSTICK", "SLOT", "DIE", "PUZZLE", "TEDDY", "PIÑATA", "MIRROR", "DOLL",
        "CARD", "MAHJONG", "FLOWER PLAYING", "PERFORMING", "ART", "THREAD", "SEWING",
        "YARN", "KNOT", "CLOTHES", "SHIRT", "JEANS", "SCARF", "GLOVES", "COAT", "SOCKS",
        "DRESS", "KIMONO", "SARI", "BIKINI", "SHOE", "BOOT", "SANDAL", "CROWN", "HAT",
        "HELMET", "GLASSES", "GOGGLES", "RING", "GEM", "LIPSTICK", "PURSE", "HANDBAG",
        "POUCH", "BACKPACK", "UMBRELLA", "WATCH", "CLOCK", "HOURGLASS", "ALARM",
        "MOBILE", "TELEPHONE", "PHONE", "PAGER", "FAX", "BATTERY", "PLUG", "COMPUTER",
        "LAPTOP", "KEYBOARD", "PRINTER", "MOUSE THREE", "TRACKBALL", "DISK", "FLOPPY",
        "CD", "DVD", "ABACUS", "CAMERA", "VIDEO", "PROJECTOR", "FILM", "TELEVISION",
        "RADIO", "MICROPHONE", "HEADPHONE", "SPEAKER", "MEGAPHONE", "BELL", "MUSICAL",
        "SAXOPHONE", "GUITAR", "TRUMPET", "VIOLIN", "BANJO", "DRUM", "MAGNIFYING",
        "LAMP", "LIGHT BULB", "TORCH", "CANDLE", "LANTERN", "BOOK", "NOTEBOOK", "LEDGER",
        "PAGE", "NEWSPAPER", "BOOKMARK", "LABEL", "MONEY", "COIN", "CREDIT", "RECEIPT",
        "ENVELOPE", "MAIL", "POSTBOX", "PENCIL", "PEN", "PAINTBRUSH", "CRAYON", "MEMO",
        "FILE", "FOLDER", "CALENDAR", "CLIPBOARD", "PUSHPIN", "PAPERCLIP", "RULER",
        "SCISSORS", "LOCK", "KEY", "HAMMER", "AXE", "PICK", "SPANNER", "WRENCH",
        "SCREWDRIVER", "NUT AND BOLT", "GEAR", "CLAMP", "BALANCE", "PROBING", "LINK",
        "CHAIN", "HOOK", "TOOLBOX", "MAGNET", "LADDER", "ALEMBIC", "TEST TUBE", "PETRI",
        "DNA", "MICROSCOPE", "TELESCOPE", "SATELLITE ANTENNA", "SYRINGE", "PILL",
        "BANDAGE", "STETHOSCOPE", "DOOR", "ELEVATOR", "BED", "COUCH", "CHAIR", "TOILET",
        "PLUNGER", "SHOWER", "BATHTUB", "SOAP", "SPONGE", "BUCKET", "RAZOR", "BROOM",
        "BASKET", "ROLL OF", "CIGARETTE", "COFFIN", "HEADSTONE", "URN", "MOYAI",
        "PLACARD", "IDENTIFICATION", "FIRECRACKER", "SPARKLER", "BALLOON", "PARTY",
        "CONFETTI", "RIBBON", "GIFT", "TICKET", "SHOPPING", "BASKETBALL", "SHIELD",
        "DAGGER", "SWORD", "GUN", "PISTOL", "BOMB", "SLINGSHOT", "BOOMERANG", "TRAP",
        "PRESENT", "GRADUATION", "SLIDER", "KNOB", "CINEMA", "CLAPPER", "DIRECT HIT",
        "BILLIARDS", "CAMPING", "FATHER CHRISTMAS", "CHRISTMAS", "JACK-O", "FIREWORK",
        "SPARKLES", "TANABATA", "WIND CHIME", "MOON VIEWING", "RED ENVELOPE", "REMINDER",
        "CROSSED FLAGS", "JAPANESE DOLLS", "CARP STREAMER", "SCROLL", "BATTERY", "TROLLEY",
    )),
    ("Symbols", (
        "HEART", "ARROW", "TRIANGLE", "SQUARE", "CIRCLE", "DIAMOND", "STAR", "CROSS",
        "CHECK", "MARK", "SIGN", "SYMBOL", "BUTTON", "RADIO", "CURRENCY", "DOLLAR",
        "RECYCLING", "TRIDENT", "FLEUR", "ATOM", "OM ", "WHEEL OF", "YIN", "LATIN",
        "PEACE", "MENORAH", "SIX POINTED", "ZODIAC", "ARIES", "TAURUS", "GEMINI",
        "CANCER", "LEO", "VIRGO", "LIBRA", "SCORPIUS", "SAGITTARIUS", "CAPRICORN",
        "AQUARIUS", "PISCES", "OPHIUCHUS", "REPEAT", "SHUFFLE", "PLAY", "PAUSE", "STOP",
        "RECORD", "EJECT", "FAST", "BLACK", "WHITE", "RED", "BLUE", "ORANGE", "YELLOW",
        "GREEN", "PURPLE", "BROWN", "DIGIT", "KEYCAP", "HUNDRED", "ANGER", "SPEECH",
        "THOUGHT", "SLEEPING", "ZZZ", "WARNING", "PROHIBITED", "NO ENTRY", "RADIOACTIVE",
        "BIOHAZARD", "EXCLAMATION", "QUESTION", "ASTERISK", "HASH", "TRADE MARK",
        "COPYRIGHT", "REGISTERED", "INFINITY", "WAVY", "DOUBLE", "MULTIPLY", "PLUS",
        "MINUS", "DIVISION", "EQUALS", "PART ALTERNATION", "EIGHT SPOKED", "SPARKLE",
    )),
]

# Characters in these blocks that are not standalone emoji. Skin-tone modifiers are the
# important ones: they are combining marks, and on their own they render as a bare colour
# swatch that means nothing in a picker.
SKIP_NAMES = ("EMOJI MODIFIER", "VARIATION SELECTOR", "ZERO WIDTH", "TAG ")

# A handful more that render as unfamiliar glyphs or blank boxes in most fonts.
SKIP = {
    0x2B12, 0x2B13, 0x2B14, 0x2B15, 0x2B16, 0x2B17, 0x2B18, 0x2B19, 0x2B1A,
    0x2B1B, 0x2B1C, 0x2B1D, 0x2B1E, 0x2B1F, 0x2B20, 0x2B21, 0x2B22, 0x2B23,
    0x2B24, 0x2B25, 0x2B26, 0x2B27, 0x2B28, 0x2B29, 0x2B2A, 0x2B2B,
}

# Country flags are pairs of regional indicators, which no Unicode name covers, so the
# common ones are listed by hand. Searching "flag" finds them all.
FLAGS = {
    "AR": "Argentina", "AT": "Austria", "AU": "Australia", "BE": "Belgium",
    "BR": "Brazil", "CA": "Canada", "CH": "Switzerland", "CL": "Chile",
    "CN": "China", "CO": "Colombia", "CZ": "Czechia", "DE": "Germany",
    "DK": "Denmark", "EE": "Estonia", "EG": "Egypt", "ES": "Spain",
    "EU": "European Union", "FI": "Finland", "FR": "France", "GB": "United Kingdom",
    "GR": "Greece", "HK": "Hong Kong", "HR": "Croatia", "HU": "Hungary",
    "ID": "Indonesia", "IE": "Ireland", "IL": "Israel", "IN": "India",
    "IS": "Iceland", "IT": "Italy", "JP": "Japan", "KE": "Kenya",
    "KR": "South Korea", "LT": "Lithuania", "LU": "Luxembourg", "LV": "Latvia",
    "MA": "Morocco", "MX": "Mexico", "MY": "Malaysia", "NG": "Nigeria",
    "NL": "Netherlands", "NO": "Norway", "NZ": "New Zealand", "PE": "Peru",
    "PH": "Philippines", "PK": "Pakistan", "PL": "Poland", "PT": "Portugal",
    "RO": "Romania", "RS": "Serbia", "RU": "Russia", "SA": "Saudi Arabia",
    "SE": "Sweden", "SG": "Singapore", "SI": "Slovenia", "SK": "Slovakia",
    "TH": "Thailand", "TR": "Türkiye", "TW": "Taiwan", "UA": "Ukraine",
    "US": "United States", "VN": "Vietnam", "ZA": "South Africa",
}


def group_for(name: str) -> str:
    for group, keywords in GROUPS:
        if any(keyword in name for keyword in keywords):
            return group
    return "Other"


def main() -> None:
    rows: list[tuple[str, str, str]] = []
    seen: set[str] = set()

    for start, end in BLOCKS:
        for code in range(start, end + 1):
            if code in SKIP:
                continue
            char = chr(code)
            try:
                name = unicodedata.name(char)
            except ValueError:
                continue          # unassigned in this Unicode version
            if any(skip in name for skip in SKIP_NAMES):
                continue
            if char in seen:
                continue
            seen.add(char)
            rows.append((char, name.title(), group_for(name)))

    for code, country in sorted(FLAGS.items(), key=lambda pair: pair[1]):
        flag = "".join(chr(0x1F1E6 + ord(letter) - ord("A")) for letter in code)
        rows.append((flag, f"Flag {country} {code}", "Flags"))

    OUT.parent.mkdir(parents=True, exist_ok=True)
    with OUT.open("w", encoding="utf-8", newline="\n") as handle:
        for char, name, group in rows:
            handle.write(f"{char}\t{name}\t{group}\n")

    counts: dict[str, int] = {}
    for _, _, group in rows:
        counts[group] = counts.get(group, 0) + 1
    print(f"{len(rows)} emoji -> {OUT.relative_to(OUT.parent.parent)}")
    for group, count in sorted(counts.items(), key=lambda pair: -pair[1]):
        print(f"  {count:5}  {group}")
    if counts.get("Other", 0) > len(rows) * 0.25:
        sys.exit("Too many ungrouped — the keyword lists need work.")


if __name__ == "__main__":
    main()

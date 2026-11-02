# Generates FormatSpecCases.txt: Python's own format() results for Taipan's format-spec tests
# (SPEC §6, M26). Each line is kind<TAB>value<TAB>spec<TAB>result, with ERROR where Python
# rejects the spec for that kind. Run with python3 from the repository root.
import itertools, math

ints = [0, 7, -7, 42, 255, -1234567, 9223372036854775807, -9223372036854775808]
floats = [0.0, -0.0, 0.5, 2.0, 0.125, 2.675, -1.5, 1234.5678, 1e16, 1.5e-7, 123456789.0, 1e-5, 0.000123, 99.99, -0.0001, math.inf, -math.inf, math.nan]
strs = ["", "a", "taipan", "héllo", "🐍x"]

specs = ["", "d", "5", "<5", ">5", "^7", "*^7", "=8", "+", " ", "-", "+5", "05", "08", "<08", "x<6", ",", "_", "010,", "09,", "_b", "#b", "#o", "#x", "#X", "x", "X", "o", "b", "08b", "c",
         ".2f", "f", ".0f", "#.0f", "10.3f", "+.1f", "z.1f", "e", ".3e", "E", "#.0e", ".2g", "g", ".10g", "#g", "G", "%", ".1%", ",.2f", "015,.2f", ".3", ".1", "10", "s", ".3s", ".3", ">8s", "0^9"]

with open("tests/Taipan.Tests/Runtime/FormatSpecCases.txt", "w", encoding="utf-8") as out:
    for kind, values in (("int", ints), ("float", floats), ("str", strs)):
        for value, spec in itertools.product(values, specs):
            try:
                result = format(value, spec)
            except (ValueError, OverflowError):
                result = "ERROR"
            if kind == "int" and spec == "c" and not (0 <= value <= 0x10FFFF):
                continue
            shown = repr(value) if kind == "float" else str(value)
            out.write(f"{kind}\t{shown}\t{spec}\t{result}\n")

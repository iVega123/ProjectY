package documents

import "testing"

func TestCnpjMatchesTheSharedDotNetRule(t *testing.T) {
	valid := []string{
		"11444777000161",
		"11.444.777/0001-61",
	}
	for _, value := range valid {
		if !ValidCnpj(value) {
			t.Fatalf("CNPJ válido recusado: %q", value)
		}
	}

	invalid := map[string]string{
		"dígito verificador errado": "11444777000162",
		"todos os dígitos iguais":   "11111111111111",
		"curto demais":              "1144477700016",
		"formato intermediário":     "11.444777/0001-61",
		"vazio":                     "",
	}
	for name, value := range invalid {
		t.Run(name, func(t *testing.T) {
			if ValidCnpj(value) {
				t.Fatalf("CNPJ inválido aceito: %q", value)
			}
		})
	}
}

func TestNormalizeKeepsOnlyDigits(t *testing.T) {
	if got := NormalizeCnpj("11.444.777/0001-61"); got != "11444777000161" {
		t.Fatalf("normalização inesperada: %q", got)
	}
}

func TestCnhNumberIsElevenDigits(t *testing.T) {
	if !ValidCnhNumber("12345678901") {
		t.Fatal("CNH de 11 dígitos recusada")
	}
	for _, value := range []string{"1234567890", "123456789012", "1234567890a", ""} {
		if ValidCnhNumber(value) {
			t.Fatalf("CNH inválida aceita: %q", value)
		}
	}
}

func TestCnhTypeIsCanonicalized(t *testing.T) {
	for input, expected := range map[string]string{"a": "A", "B": "B", " ab ": "AB"} {
		got, err := ParseCnhType(input)
		if err != nil || got != expected {
			t.Fatalf("ParseCnhType(%q) = %q, %v", input, got, err)
		}
	}
	if _, err := ParseCnhType("C"); err == nil {
		t.Fatal("tipo desconhecido aceito")
	}
}

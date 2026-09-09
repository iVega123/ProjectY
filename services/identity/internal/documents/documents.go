// Package documents valida os dois documentos brasileiros que o cadastro de
// piloto exige.
//
// O CNPJ é uma porta de [ProjectY.Shared.Validation.BrazilianCnpj], em C#, e a
// fidelidade é o ponto: os mesmos pesos, a mesma recusa de dígitos todos iguais
// e os mesmos dois formatos aceitos. Uma regra de validação que diverge entre
// dois serviços produz cadastro que um aceita e o outro considera corrompido --
// e o achado B10 já mostrou o que acontece quando duas camadas discordam em
// silêncio sobre a identidade de um piloto.
package documents

import (
	"fmt"
	"regexp"
	"strings"
)

var (
	nonDigits  = regexp.MustCompile(`[^0-9]`)
	cnpjFormat = regexp.MustCompile(`^(?:[0-9]{14}|[0-9]{2}\.[0-9]{3}\.[0-9]{3}/[0-9]{4}-[0-9]{2})$`)
	cnhFormat  = regexp.MustCompile(`^[0-9]{11}$`)
)

// NormalizeCnpj devolve só os dígitos.
func NormalizeCnpj(value string) string {
	return nonDigits.ReplaceAllString(value, "")
}

// ValidCnpj confere formato e dígitos verificadores.
func ValidCnpj(value string) bool {
	if !cnpjFormat.MatchString(strings.TrimSpace(value)) {
		return false
	}
	digits := NormalizeCnpj(value)
	if strings.Count(digits, digits[0:1]) == len(digits) {
		return false
	}
	return checkDigit(digits[:12], []int{5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2}) == int(digits[12]-'0') &&
		checkDigit(digits[:13], []int{6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2}) == int(digits[13]-'0')
}

func checkDigit(digits string, weights []int) int {
	sum := 0
	for index, digit := range digits {
		sum += int(digit-'0') * weights[index]
	}
	if remainder := sum % 11; remainder >= 2 {
		return 11 - remainder
	}
	return 0
}

// ValidCnhNumber exige os onze dígitos que o registro nacional tem.
func ValidCnhNumber(value string) bool {
	return cnhFormat.MatchString(strings.TrimSpace(value))
}

// ParseCnhType aceita A, B e AB, em qualquer caixa, e devolve a forma
// canônica -- que é a mesma que o CHECK de `riders.cnh_type` conhece.
func ParseCnhType(value string) (string, error) {
	switch strings.ToUpper(strings.TrimSpace(value)) {
	case "A":
		return "A", nil
	case "B":
		return "B", nil
	case "AB":
		return "AB", nil
	default:
		return "", fmt.Errorf("tipo de CNH inválido: %q", value)
	}
}

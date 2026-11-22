package auth

import (
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"math/big"
	"strings"
	"time"
)

// ErrInvalidToken covers every access-token failure; callers must not learn which check failed.
var ErrInvalidToken = errors.New("invalid access token")

const (
	tokenIssuer   = "alondra1"
	tokenAudience = "alondra1-api"
	// The only header ever produced or accepted. Fixing it (instead of reading "alg") rules out alg=none and key confusion.
	accessHeader = `{"alg":"HS256","typ":"JWT"}`
	// RefreshTokenPrefix makes leaked refresh tokens recognizable by secret scanners.
	RefreshTokenPrefix = "a1r_"
	clockLeeway        = 30 * time.Second
)

var encodedAccessHeader = base64.RawURLEncoding.EncodeToString([]byte(accessHeader))

// AccessClaims are the claims of a short-lived access token.
type AccessClaims struct {
	Issuer    string `json:"iss"`
	Audience  string `json:"aud"`
	Subject   string `json:"sub"` // user id
	SessionID string `json:"sid"`
	IssuedAt  int64  `json:"iat"`
	ExpiresAt int64  `json:"exp"`
	Type      string `json:"typ"`
}

// TokenSigner issues and verifies HS256 access tokens with one server-only key.
type TokenSigner struct {
	key      []byte
	previous [][]byte // accepted for verification only, during a key rotation
}

func NewTokenSigner(key []byte) (*TokenSigner, error) {
	if len(key) < 32 {
		return nil, errors.New("token key must be at least 32 bytes")
	}
	return &TokenSigner{key: append([]byte(nil), key...)}, nil
}

// WithPrevious also accepts tokens signed with older keys (key rotation: tokens issued before the switch keep working
// until they expire). New tokens are always signed with the current key.
func (s *TokenSigner) WithPrevious(keys ...[]byte) *TokenSigner {
	for _, k := range keys {
		if len(k) > 0 {
			s.previous = append(s.previous, append([]byte(nil), k...))
		}
	}
	return s
}

func (s *TokenSigner) Issue(userID, sessionID string, now time.Time, ttl time.Duration) (string, error) {
	claims := AccessClaims{
		Issuer: tokenIssuer, Audience: tokenAudience, Subject: userID, SessionID: sessionID,
		IssuedAt: now.Unix(), ExpiresAt: now.Add(ttl).Unix(), Type: "access",
	}
	payload, err := json.Marshal(claims)
	if err != nil {
		return "", err
	}
	signingInput := encodedAccessHeader + "." + base64.RawURLEncoding.EncodeToString(payload)
	return signingInput + "." + base64.RawURLEncoding.EncodeToString(s.sign(signingInput)), nil
}

func (s *TokenSigner) Verify(token string, now time.Time) (AccessClaims, error) {
	parts := strings.Split(token, ".")
	if len(parts) != 3 || parts[0] != encodedAccessHeader {
		return AccessClaims{}, ErrInvalidToken
	}
	signature, err := base64.RawURLEncoding.DecodeString(parts[2])
	if err != nil || !s.signedByAnyKey(parts[0]+"."+parts[1], signature) {
		return AccessClaims{}, ErrInvalidToken
	}
	payload, err := base64.RawURLEncoding.DecodeString(parts[1])
	if err != nil {
		return AccessClaims{}, ErrInvalidToken
	}
	var claims AccessClaims
	if err := json.Unmarshal(payload, &claims); err != nil {
		return AccessClaims{}, ErrInvalidToken
	}
	if claims.Issuer != tokenIssuer || claims.Audience != tokenAudience || claims.Type != "access" ||
		claims.Subject == "" || claims.SessionID == "" ||
		now.After(time.Unix(claims.ExpiresAt, 0).Add(clockLeeway)) ||
		time.Unix(claims.IssuedAt, 0).After(now.Add(clockLeeway)) {
		return AccessClaims{}, ErrInvalidToken
	}
	return claims, nil
}

func (s *TokenSigner) sign(input string) []byte {
	mac := hmac.New(sha256.New, s.key)
	mac.Write([]byte(input))
	return mac.Sum(nil)
}

func (s *TokenSigner) signedByAnyKey(input string, signature []byte) bool {
	if hmac.Equal(signature, s.sign(input)) {
		return true
	}
	for _, key := range s.previous {
		mac := hmac.New(sha256.New, key)
		mac.Write([]byte(input))
		if hmac.Equal(signature, mac.Sum(nil)) {
			return true
		}
	}
	return false
}

// NewRefreshToken returns an opaque 256-bit refresh token and the hash stored server-side. A plain SHA-256 is enough
// because the token is uniformly random; the token itself is never stored or logged.
func NewRefreshToken() (token string, hash []byte, err error) {
	raw := make([]byte, 32)
	if _, err := rand.Read(raw); err != nil {
		return "", nil, err
	}
	token = RefreshTokenPrefix + base64.RawURLEncoding.EncodeToString(raw)
	return token, HashRefreshToken(token), nil
}

func HashRefreshToken(token string) []byte {
	sum := sha256.Sum256([]byte(token))
	return sum[:]
}

// NewOTP returns a uniformly random 6-digit code from the OS CSPRNG.
func NewOTP() (string, error) {
	n, err := rand.Int(rand.Reader, big.NewInt(1_000_000))
	if err != nil {
		return "", err
	}
	return fmt.Sprintf("%06d", n.Int64()), nil
}

// OTPMAC binds a code to its challenge and email with the server-only OTP key, so a database leak reveals no codes and a
// code cannot be replayed against another challenge.
func OTPMAC(key []byte, challengeID, email, code string) []byte {
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte("otp\x00" + challengeID + "\x00" + email + "\x00" + code))
	return mac.Sum(nil)
}

// IPHash keys a client IP so rate limits work without storing raw addresses.
func IPHash(key []byte, ip string) []byte {
	mac := hmac.New(sha256.New, key)
	mac.Write([]byte("ip\x00" + ip))
	return mac.Sum(nil)
}

// NewUUIDv7 returns a time-ordered UUID (RFC 9562) for primary keys.
func NewUUIDv7(now time.Time) (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[6:]); err != nil {
		return "", err
	}
	ms := uint64(now.UnixMilli())
	binary.BigEndian.PutUint16(b[0:2], uint16(ms>>32))
	binary.BigEndian.PutUint32(b[2:6], uint32(ms))
	b[6] = (b[6] & 0x0f) | 0x70
	b[8] = (b[8] & 0x3f) | 0x80
	return formatUUID(b), nil
}

// NewUUIDv4 returns a fully random UUID, used where an id must not reveal its creation time (challenge ids).
func NewUUIDv4() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return formatUUID(b), nil
}

func formatUUID(b [16]byte) string {
	h := hex.EncodeToString(b[:])
	return h[0:8] + "-" + h[8:12] + "-" + h[12:16] + "-" + h[16:20] + "-" + h[20:32]
}

// IsUUID reports whether s looks like a canonical lowercase UUID; used to reject garbage ids before touching the DB.
func IsUUID(s string) bool {
	if len(s) != 36 {
		return false
	}
	for i, c := range s {
		switch i {
		case 8, 13, 18, 23:
			if c != '-' {
				return false
			}
		default:
			if !(c >= '0' && c <= '9' || c >= 'a' && c <= 'f') {
				return false
			}
		}
	}
	return true
}

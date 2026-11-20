// Pocket XR for Windows: moonlight-common-c's crypto (PlatformCrypto.h) on Windows' own CNG (bcrypt.dll), in place
// of PlatformCrypto.c and OpenSSL. Same behaviour as the OpenSSL path there:
// - AES-128-GCM: one message per call, tag separate (any IV length; a fresh IV every call).
// - AES-128-CBC decrypt with CIPHER_FLAG_RESET_IV | CIPHER_FLAG_FINISH: one message, PKCS#7 padding removed.
// - AES-128-CBC encrypt with CIPHER_FLAG_PAD_TO_BLOCK_SIZE: PKCS#7 padded in place, the chain carried across calls
//   until CIPHER_FLAG_RESET_IV (input on hosts before generation 7).
// GPLv3, as moonlight-common-c.

#include <windows.h>
#include <bcrypt.h>
#include <stdlib.h>
#include <string.h>

#include "PlatformCrypto.h"

typedef struct {
    BCRYPT_KEY_HANDLE key;
    int mode; // 0 none yet, ALGORITHM_AES_GCM or ALGORITHM_AES_CBC
    unsigned char iv[16];
    bool cbcChained;
} PXR_CRYPTO;

static BCRYPT_ALG_HANDLE gcmAlg, cbcAlg;
static INIT_ONCE algOnce = INIT_ONCE_STATIC_INIT;

static BOOL CALLBACK openAlgs(PINIT_ONCE once, PVOID param, PVOID* ctx) {
    (void)once; (void)param; (void)ctx;
    if (BCryptOpenAlgorithmProvider(&gcmAlg, BCRYPT_AES_ALGORITHM, NULL, 0) != 0) return FALSE;
    BCryptSetProperty(gcmAlg, BCRYPT_CHAINING_MODE, (PUCHAR)BCRYPT_CHAIN_MODE_GCM, sizeof(BCRYPT_CHAIN_MODE_GCM), 0);
    if (BCryptOpenAlgorithmProvider(&cbcAlg, BCRYPT_AES_ALGORITHM, NULL, 0) != 0) return FALSE;
    BCryptSetProperty(cbcAlg, BCRYPT_CHAINING_MODE, (PUCHAR)BCRYPT_CHAIN_MODE_CBC, sizeof(BCRYPT_CHAIN_MODE_CBC), 0);
    return TRUE;
}

PPLT_CRYPTO_CONTEXT PltCreateCryptoContext(void) {
    if (!InitOnceExecuteOnce(&algOnce, openAlgs, NULL, NULL)) return NULL;
    PXR_CRYPTO* c = (PXR_CRYPTO*)calloc(1, sizeof(PXR_CRYPTO));
    return (PPLT_CRYPTO_CONTEXT)c;
}

void PltDestroyCryptoContext(PPLT_CRYPTO_CONTEXT ctx) {
    PXR_CRYPTO* c = (PXR_CRYPTO*)ctx;
    if (c == NULL) return;
    if (c->key) BCryptDestroyKey(c->key);
    SecureZeroMemory(c, sizeof(*c));
    free(c);
}

static bool ensureKey(PXR_CRYPTO* c, int algorithm, unsigned char* key, int keyLength) {
    if (c->key && c->mode == algorithm) return true;
    if (c->key) { BCryptDestroyKey(c->key); c->key = NULL; }
    BCRYPT_ALG_HANDLE alg = algorithm == ALGORITHM_AES_GCM ? gcmAlg : cbcAlg;
    if (BCryptGenerateSymmetricKey(alg, &c->key, NULL, 0, key, (ULONG)keyLength, 0) != 0) return false;
    c->mode = algorithm;
    c->cbcChained = false;
    return true;
}

static void pkcs7Pad(unsigned char* data, int len, int* padded) {
    int p = ROUND_TO_PKCS7_PADDED_LEN(len);
    memset(data + len, (unsigned char)(16 - (len % 16)), (size_t)(p - len));
    *padded = p;
}

static bool crypt(bool encrypt, PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                  unsigned char* key, int keyLength, unsigned char* iv, int ivLength,
                  unsigned char* tag, int tagLength, unsigned char* in, int inLen,
                  unsigned char* out, int* outLen) {
    PXR_CRYPTO* c = (PXR_CRYPTO*)ctx;
    if (c == NULL || !ensureKey(c, algorithm, key, keyLength)) return false;
    ULONG done = 0;
    NTSTATUS st;

    if (algorithm == ALGORITHM_AES_GCM) {
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO info;
        BCRYPT_INIT_AUTH_MODE_INFO(info);
        info.pbNonce = iv;
        info.cbNonce = (ULONG)ivLength;
        info.pbTag = tag;
        info.cbTag = (ULONG)tagLength;
        st = encrypt
            ? BCryptEncrypt(c->key, in, (ULONG)inLen, &info, NULL, 0, out, (ULONG)inLen, &done, 0)
            : BCryptDecrypt(c->key, in, (ULONG)inLen, &info, NULL, 0, out, (ULONG)inLen, &done, 0);
        if (st != 0) return false;
        *outLen = (int)done;
        return true;
    }

    if (algorithm != ALGORITHM_AES_CBC || ivLength > 16) return false;
    if (!c->cbcChained || (flags & CIPHER_FLAG_RESET_IV)) {
        memset(c->iv, 0, sizeof(c->iv));
        memcpy(c->iv, iv, (size_t)ivLength);
        c->cbcChained = true;
    }
    if (encrypt) {
        int len = inLen;
        if (flags & CIPHER_FLAG_PAD_TO_BLOCK_SIZE) {
            pkcs7Pad(in, inLen, &len);
            st = BCryptEncrypt(c->key, in, (ULONG)len, NULL, c->iv, 16, out, (ULONG)len, &done, 0);
        } else {
            // CIPHER_FLAG_FINISH: PKCS#7 added by CNG; output is the padded length.
            st = BCryptEncrypt(c->key, in, (ULONG)len, NULL, c->iv, 16, out, (ULONG)ROUND_TO_PKCS7_PADDED_LEN(len), &done,
                               (flags & CIPHER_FLAG_FINISH) ? BCRYPT_BLOCK_PADDING : 0);
        }
    } else {
        st = BCryptDecrypt(c->key, in, (ULONG)inLen, NULL, c->iv, 16, out, (ULONG)inLen, &done,
                           (flags & CIPHER_FLAG_FINISH) ? BCRYPT_BLOCK_PADDING : 0);
    }
    if (st != 0) return false;
    if (flags & CIPHER_FLAG_FINISH) c->cbcChained = false;
    *outLen = (int)done;
    return true;
}

bool PltEncryptMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                       unsigned char* key, int keyLength, unsigned char* iv, int ivLength,
                       unsigned char* tag, int tagLength, unsigned char* inputData, int inputDataLength,
                       unsigned char* outputData, int* outputDataLength) {
    return crypt(true, ctx, algorithm, flags, key, keyLength, iv, ivLength, tag, tagLength,
                 inputData, inputDataLength, outputData, outputDataLength);
}

bool PltDecryptMessage(PPLT_CRYPTO_CONTEXT ctx, int algorithm, int flags,
                       unsigned char* key, int keyLength, unsigned char* iv, int ivLength,
                       unsigned char* tag, int tagLength, unsigned char* inputData, int inputDataLength,
                       unsigned char* outputData, int* outputDataLength) {
    return crypt(false, ctx, algorithm, flags, key, keyLength, iv, ivLength, tag, tagLength,
                 inputData, inputDataLength, outputData, outputDataLength);
}

void PltGenerateRandomData(unsigned char* data, int length) {
    BCryptGenRandom(NULL, data, (ULONG)length, BCRYPT_USE_SYSTEM_PREFERRED_RNG);
}

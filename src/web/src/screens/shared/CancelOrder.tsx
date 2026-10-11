import { useState } from 'react';
import { ConfirmStep, TextField } from '../../components/forms';
import type { Disclosure } from '../../components/forms';
import { formatQuantity } from '../../lib/format';

interface CancelOrderProps {
  disclosure: Disclosure;
  formId: string;
  /** Quantity that is still open and would be cancelled. */
  openQty: number;
  /** Quantity already shipped / received; it stays. */
  fulfilledQty: number;
  fulfilledWord: 'sevk edilen' | 'kabul edilen';
  /** A confirmed sales order holds a reservation that the cancellation releases. */
  releasesReservation: boolean;
  pending: boolean;
  serverError?: string | undefined;
  onConfirm: (reason: string) => void;
}

/** Inline confirmation of a cancellation: says what is lost and what is kept, and asks for the reason. */
export function CancelOrder({
  disclosure,
  formId,
  openQty,
  fulfilledQty,
  fulfilledWord,
  releasesReservation,
  pending,
  serverError,
  onConfirm,
}: CancelOrderProps) {
  // Kept while the step is closed and across a 412 reload, so the user does not retype it.
  const [reason, setReason] = useState('');
  const [error, setError] = useState<string | undefined>(undefined);
  const partial = fulfilledQty > 0;

  const confirm = () => {
    const text = reason.trim();
    if (text.length < 3 || text.length > 300) {
      setError('İptal gerekçesi 3–300 karakter olmalıdır.');
      document.getElementById(`${formId}-reason`)?.focus();
      return;
    }

    setError(undefined);
    onConfirm(text);
  };

  return (
    <ConfirmStep
      disclosure={disclosure}
      confirmLabel={partial ? 'Kalanı iptal et' : 'Siparişi iptal et'}
      pendingLabel="İptal ediliyor…"
      pending={pending}
      onConfirm={confirm}
      question={
        <>
          <strong>{partial ? `Kalan ${formatQuantity(openQty)} adet iptal edilsin mi?` : 'Sipariş iptal edilsin mi?'}</strong>{' '}
          {releasesReservation && openQty > 0 && 'Rezervasyon serbest bırakılır. '}
          {partial && `${formatQuantity(fulfilledQty)} adet ${fulfilledWord} miktar korunur. `}
          Sipariş “İptal edildi” durumuna geçer ve yeniden açılamaz.
        </>
      }
    >
      <TextField
        formId={formId}
        name="reason"
        label="İptal gerekçesi"
        value={reason}
        onChange={(value) => {
          setReason(value);
          if (error !== undefined) {
            setError(undefined);
          }
        }}
        required
        maxLength={300}
        fieldClassName="measure-form"
        placeholder="Örn. müşteri kalan miktardan vazgeçti…"
        error={error ?? serverError}
      />
    </ConfirmStep>
  );
}

from __future__ import annotations

from datetime import datetime, timedelta
from typing import Dict, List, Set, Tuple
from uuid import UUID, uuid4

import crypto
from cert import Certificate
from mass import Agent
from myrepr import ReprObject
from payments import Invoice, PaymentChannel, compute_payment_hash
from pow import ProofOfWork, WorkRequest, pow_target_from_complexity

from numpy import argmin

class SignableObject(ReprObject):
    def sign(self, private_key: bytes) -> None:
        self.signature = None
        self.signature = crypto.sign_object(self, private_key)

    def verify(self, public_key: bytes) -> bool:
        signature = self.signature
        self.signature = None
        result = crypto.verify_object(self, signature, public_key)
        self.signature = signature
        return result


class OnionLayer(ReprObject):
    def __init__(self, peer_name: str) -> None:
        self.peer_name = peer_name


class OnionRoute(ReprObject):
    def __init__(self) -> None:
        self._onion = b""

    def peel(self, priv_key: bytes) -> OnionLayer:
        layer, rest = crypto.decrypt_object(self._onion, priv_key)
        self._onion = rest
        return layer

    def grow(self, layer: OnionLayer, pub_key: bytes) -> OnionRoute:
        new_onion = OnionRoute()
        new_onion._onion = crypto.encrypt_object((layer, self._onion), pub_key)
        return new_onion

    def is_empty(self) -> bool:
        return len(self._onion) == 0


class RoutingPaymentInstruction(SignableObject):
    def __init__(self, account: bytes, amount: int, public_key: bytes) -> None:
        self.account = account
        self.amount = amount
        self.public_key = public_key

    def to_tuple(self) -> Tuple[bytes, int, bytes]:
        return (self.account, self.amount, self.public_key)


class AbstractTopic(ReprObject):
    pass


class RequestPayload(SignableObject):
    def __init__(self, id: UUID, topic: AbstractTopic, sender_certificate: Certificate) -> None:
        self.id = id
        self.topic = topic
        self.sender_certificate = sender_certificate


class AskForBroadcastFrame(ReprObject):
    def __init__(self, signed_request_payload: RequestPayload) -> None:
        self.ask_id = uuid4()
        self.signed_request_payload = signed_request_payload


class POWBroadcastConditionsFrame(ReprObject):
    def __init__(self, ask_id: UUID, valid_till: datetime, work_request: WorkRequest, routing_payment_instruction: RoutingPaymentInstruction) -> None:
        self.ask_id = ask_id
        self.valid_till = valid_till
        self.work_request = work_request
        self.routing_payment_instruction = routing_payment_instruction


class BroadcastPayload(ReprObject):
    def __init__(self,
                 signed_request_payload: RequestPayload,
                 backward_onion: OnionRoute,
                 routing_payment_instruction_list: List[RoutingPaymentInstruction]
                 ):
        self.signed_request_payload = signed_request_payload
        self.backward_onion = backward_onion
        self.routing_payment_instruction_list = routing_payment_instruction_list


class POWBroadcastFrame(ReprObject):
    def __init__(self,
                 ask_id: UUID,
                 broadcast_payload: BroadcastPayload,
                 proof_of_work: ProofOfWork
                 ):
        self.ask_id = ask_id
        self.broadcast_payload = broadcast_payload
        self.proof_of_work = proof_of_work

    def verify(self) -> bool:
        if not self.broadcast_payload.signed_request_payload.sender_certificate.verify():
            return False

        if not self.broadcast_payload.signed_request_payload.verify(self.broadcast_payload.signed_request_payload.sender_certificate.public_key):
            return False

        return self.proof_of_work.validate(self.broadcast_payload)


class PaymentCryptoInstruction(ReprObject):
    def __init__(self, account: bytes, amount: int, preimage: bytes, public_key: bytes) -> None:
        self.account = account
        self.amount = amount
        self.encrypted_preimage = crypto.encrypt_object(preimage, public_key)
        self.payment_hash = compute_payment_hash(preimage)


class ReplyPayload(SignableObject):
    def __init__(self,
                 signed_request_payload: RequestPayload,
                 payment_crypto_instruction_list: List[PaymentCryptoInstruction],
                 encrypted_reply_message: bytes,
                 invoice: Invoice
                 ) -> None:
        self.signed_request_payload = signed_request_payload
        self.encrypted_reply_message = encrypted_reply_message
        self.payment_crypto_instruction_list = payment_crypto_instruction_list
        self.invoice = invoice

    def verify_all(self, replier_public_key: bytes):
        if not self.verify(replier_public_key):
            return False
        if not self.signed_request_payload.sender_certificate.verify():
            return False
        if not self.signed_request_payload.verify(self.signed_request_payload.sender_certificate.public_key):
            return False
        return True


class ResponseFrame(ReprObject):
    def __init__(self,
                 replier_certificate: Certificate,
                 routing_payment_instruction_list: List[RoutingPaymentInstruction],
                 preimage_list: List[bytes],
                 forward_onion: OnionRoute,
                 signed_request_payload: RequestPayload,
                 message: bytes,
                 invoice: Invoice) -> None:
        self.replier_certificate = replier_certificate
        self.signed_reply_payload = ReplyPayload(signed_request_payload,
                                                 [PaymentCryptoInstruction(routing_payment_instruction.account,
                                                                           routing_payment_instruction.amount,
                                                                           preimage,
                                                                           routing_payment_instruction.public_key) for preimage, routing_payment_instruction in zip(
                                                     preimage_list, routing_payment_instruction_list)],
                                                 self._encrypt(
                                                     message, signed_request_payload.sender_certificate.public_key, preimage_list),
                                                 invoice)
        self.forward_onion = forward_onion
        self.invoices: List[Invoice] = list()

    def sign(self, replier_private_key: bytes) -> None:
        self.signed_reply_payload.sign(replier_private_key)

    def _encrypt(self, message: bytes, sender_public_key: bytes, preimage_list: List[bytes]) -> bytes:
        data = message
        data = crypto.encrypt_object(data, sender_public_key)
        for key in preimage_list:
            data = crypto.symmetric_encrypt(key, data)
        return data

    def make_invoice(self, idx: int, broadcaster_payment_channel: PaymentChannel, valid_till: datetime, private_key: bytes) -> Invoice:
        payment_crypto_instruction = self.signed_reply_payload.payment_crypto_instruction_list[
            idx]
        preimage = crypto.decrypt_object(
            payment_crypto_instruction.encrypted_preimage, private_key)
        payment_hash = compute_payment_hash(preimage)
        if payment_hash == payment_crypto_instruction.payment_hash:
            if payment_crypto_instruction.account == broadcaster_payment_channel.account:
                return broadcaster_payment_channel.create_invoice(payment_crypto_instruction.amount, preimage, valid_till)
        return None

    def invoices_are_coherent_with_signed_reply_payload(self):
        sorted_invoices = sorted(
            self.invoices, key=lambda invoice: invoice.account)
        sorted_payment_crypto_instruction_list = sorted(
            self.signed_reply_payload.payment_crypto_instruction_list, key=lambda payment_crypto_instructio: payment_crypto_instructio.account)
        payment_list_a = [
            (invoice.account, invoice.amount, invoice.payment_hash) for invoice in sorted_invoices]
        payment_list_b = [(payment_crypto_instruction.account, payment_crypto_instruction.amount, payment_crypto_instruction.payment_hash)
                          for payment_crypto_instruction in sorted_payment_crypto_instruction_list]
        if payment_list_a != payment_list_b:
            return False

        return True

    def find_route_payment_layer(self, account: bytes, amount: int) -> bool:
        for idx, payment_crypto_instruction in enumerate(self.signed_reply_payload.payment_crypto_instruction_list):
            if payment_crypto_instruction.account == account and payment_crypto_instruction.amount == amount:
                return idx
        return -1

    def verify(self):
        if not self.replier_certificate.verify():
            return False
        if not self.signed_reply_payload.verify_all(self.replier_certificate.public_key):
            return False
        return True


class Offer(ReprObject):
    def __init__(self, repier_certificate: Certificate, network_price: int, offer_price: int) -> None:
        self.repier_certificate = repier_certificate
        self.network_price = network_price
        self.offer_price = offer_price


class SweetGossipNode(Agent):
    def __init__(self,
                 name,
                 certificate: Certificate,
                 private_key: bytes,
                 payment_channel: PaymentChannel,
                 price_amount_for_routing: int,
                 broadcast_conditions_timeout: timedelta,
                 broadcast_conditions_pow_scheme: str,
                 broadcast_conditions_pow_complexity: int,
                 invoice_payment_timeout: timedelta):
        super().__init__(name)
        self.name = name
        self.certificate = certificate
        self._private_key = private_key
        self.payment_channel = payment_channel
        self.price_amount_for_routing = price_amount_for_routing
        self.broadcast_conditions_timeout = broadcast_conditions_timeout
        self.broadcast_conditions_pow_scheme = broadcast_conditions_pow_scheme
        self.broadcast_conditions_pow_complexity = broadcast_conditions_pow_complexity
        self.invoice_payment_timeout = invoice_payment_timeout

        self._known_hosts: Dict[str, SweetGossipNode] = dict()
        self._broadcast_payloads_by_ask_id: Dict[UUID, BroadcastPayload] = dict(
        )
        self._my_pow_br_cond_by_ask_id: Dict[UUID,
                                             POWBroadcastConditionsFrame] = dict()
        self._already_broadcasted_request_payload_ids: Dict[UUID, int] = dict()
        self._tuples_to_preimages: Dict[UUID,
                                        Dict[Tuple[bytes, int, bytes], bytes]] = dict()
        self.response_frames: Dict[UUID,
                                   Dict[bytes, List[ResponseFrame]]] = dict()

    def connect_to(self, other):
        if other.name == self.name:
            raise Exception("Cannot connect node to itself")
        self._known_hosts[other.name] = other
        other._known_hosts[self.name] = self

    def accept_topic(self, topic: AbstractTopic) -> bool:
        return False

    def increment_broadcasted(self, topic_id: int) -> None:
        if not topic_id in self._already_broadcasted_request_payload_ids:
            self._already_broadcasted_request_payload_ids[topic_id] = 0
        self._already_broadcasted_request_payload_ids[topic_id] += 1

    def can_increment_broadcast(self, topic_id: int) -> bool:
        if not topic_id in self._already_broadcasted_request_payload_ids:
            return True
        return self._already_broadcasted_request_payload_ids[topic_id] <= 2

    def broadcast(self, e,
                  request_payload: RequestPayload,
                  originator_peer_name: str = None,
                  backward_onion: OnionRoute = OnionRoute(),
                  routing_payment_instruction_list: List[RoutingPaymentInstruction] = list()):
        if not self.accept_topic(request_payload.topic):
            return

        self.increment_broadcasted(request_payload.id)

        if not self.can_increment_broadcast(request_payload.id):
            self.info(e, "already broadcasted")
            return

        if originator_peer_name is not None:
            routing_payment_instruction_list.append(
                RoutingPaymentInstruction(self.payment_channel.account, self.price_amount_for_routing, self.certificate.public_key))

        for peer in self._known_hosts.values():
            if peer.name == originator_peer_name:
                continue
            print(self.name, "================>>>>>>>>>", peer.name)
            ask_for_broadcast_frame = AskForBroadcastFrame(request_payload)
            broadcast_payload = BroadcastPayload(request_payload,
                                                 backward_onion.grow(OnionLayer(
                                                     self.name), peer.certificate.public_key),
                                                 routing_payment_instruction_list)
            self._broadcast_payloads_by_ask_id[ask_for_broadcast_frame.ask_id] = broadcast_payload
            self.new_message(e, peer, ask_for_broadcast_frame)

    def on_ask_for_broadcast_frame(self, e, m, peer: SweetGossipNode, ask_for_broadcast_frame: AskForBroadcastFrame):
        if not self.can_increment_broadcast(ask_for_broadcast_frame.signed_request_payload.id):
            self.info(e, "already broadcasted dont ask")
            return
        pow_broadcast_conditions_frame = POWBroadcastConditionsFrame(
            ask_id=ask_for_broadcast_frame.ask_id,
            valid_till=datetime.now()+self.broadcast_conditions_timeout,
            work_request=WorkRequest(pow_scheme=self.broadcast_conditions_pow_scheme,
                                     pow_target=pow_target_from_complexity(
                                         self.broadcast_conditions_pow_scheme, self.broadcast_conditions_pow_complexity)),
            routing_payment_instruction=RoutingPaymentInstruction(self.payment_channel.account, self.price_amount_for_routing, self.certificate.public_key))
        self._my_pow_br_cond_by_ask_id[pow_broadcast_conditions_frame.ask_id] = pow_broadcast_conditions_frame
        self.new_message(e, peer, pow_broadcast_conditions_frame)

    def on_pow_broadcast_conditions_frame(self, e, m, peer: SweetGossipNode, pow_broadcast_condtitions_frame: POWBroadcastConditionsFrame):
        if datetime.now() <= pow_broadcast_condtitions_frame.valid_till:
            if pow_broadcast_condtitions_frame.ask_id in self._broadcast_payloads_by_ask_id:
                broadcast_payload = self._broadcast_payloads_by_ask_id[
                    pow_broadcast_condtitions_frame.ask_id]
                pow_broadcast_frame = POWBroadcastFrame(pow_broadcast_condtitions_frame.ask_id,
                                                        broadcast_payload,
                                                        pow_broadcast_condtitions_frame.work_request.compute_proof(
                                                            broadcast_payload))
                self.new_message(e, peer, pow_broadcast_frame)

    def accept_broadcast(self, signed_request_payload: RequestPayload) -> Tuple[bytes, int]:
        return None, 0

    def on_pow_broadcast_frame(self, e, m, peer: SweetGossipNode, pow_broadcast_frame: POWBroadcastFrame):

        if not pow_broadcast_frame.ask_id in self._my_pow_br_cond_by_ask_id:
            return

        my_pow_broadcast_condition_frame = self._my_pow_br_cond_by_ask_id[
            pow_broadcast_frame.ask_id]

        if pow_broadcast_frame.proof_of_work.pow_scheme != my_pow_broadcast_condition_frame.work_request.pow_scheme:
            return

        if pow_broadcast_frame.proof_of_work.pow_target != my_pow_broadcast_condition_frame.work_request.pow_target:
            return

        if not pow_broadcast_frame.verify():
            return

        message, fee = self.accept_broadcast(
            pow_broadcast_frame.broadcast_payload.signed_request_payload)

        if message is not None:
            routing_payment_instruction_list = pow_broadcast_frame.broadcast_payload.routing_payment_instruction_list
            routing_payment_instruction_list.append(
                my_pow_broadcast_condition_frame.routing_payment_instruction)
            preimage_list = list()

            for routing_payment_instruction in routing_payment_instruction_list:
                topic_id = pow_broadcast_frame.broadcast_payload.signed_request_payload.id
                tpl = routing_payment_instruction.to_tuple()
                if not topic_id in self._tuples_to_preimages:
                    self._tuples_to_preimages[topic_id] = dict()
                if not tpl in self._tuples_to_preimages[topic_id]:
                    self._tuples_to_preimages[topic_id][tpl] = crypto.generate_symmetric_key(
                    )
                preimage_list.append(self._tuples_to_preimages[topic_id][tpl])

            invoice = self.payment_channel.create_invoice(fee)
            response_frame = ResponseFrame(
                replier_certificate=self.certificate,
                routing_payment_instruction_list=routing_payment_instruction_list,
                preimage_list=preimage_list,
                forward_onion=pow_broadcast_frame.broadcast_payload.backward_onion,
                signed_request_payload=pow_broadcast_frame.broadcast_payload.signed_request_payload,
                message=message,
                invoice=invoice
            )
            response_frame.sign(replier_private_key=self._private_key)
            self.on_response_frame(
                e, m, peer, response_frame=response_frame)
        else:
            self.broadcast(e, request_payload=pow_broadcast_frame.broadcast_payload.signed_request_payload,
                           originator_peer_name=peer.name,
                           backward_onion=pow_broadcast_frame.broadcast_payload.backward_onion,
                           routing_payment_instruction_list=pow_broadcast_frame.broadcast_payload.routing_payment_instruction_list)

    def on_response_frame(self, e, m, peer: SweetGossipNode, response_frame: ResponseFrame):
        if not response_frame.verify():
            return
        if response_frame.forward_onion.is_empty():
            if response_frame.invoices_are_coherent_with_signed_reply_payload():
                topic_id = response_frame.signed_reply_payload.signed_request_payload.id
                if not topic_id in self.response_frames:
                    self.response_frames[topic_id] = dict()
                replier_id = response_frame.replier_certificate.public_key
                if not replier_id in self.response_frames[topic_id]:
                    self.response_frames[topic_id][replier_id] = list()
                self.response_frames[topic_id][replier_id].append(
                    response_frame)
                self.info(e, "response frame collected")
        else:
            idx = response_frame.find_route_payment_layer(
                self.payment_channel.account,
                self.price_amount_for_routing)
            if idx >= 0:
                top_layer = response_frame.forward_onion.peel(
                    self._private_key)
                if top_layer.peer_name in self._known_hosts:
                    invoice = response_frame.make_invoice(idx,
                                                          self.payment_channel, datetime.now()+self.invoice_payment_timeout, self._private_key)
                    if invoice is not None:
                        response_frame.invoices.append(invoice)
                        self.new_message(
                            e, self._known_hosts[top_layer.peer_name], response_frame)
                    else:
                        self.error(e, "make invoice error",
                                   self.payment_channel)

    def get_offers(self, e, topic_id) -> List[Offer]:
        if not topic_id in self.response_frames:
            self.error(e, "topic has no responses")
            return list()
        return [Offer(self.response_frames[topic_id][key][0].replier_certificate,
                      sum(
                          a.amount for a in self.response_frames[topic_id][key][0].invoices),
                      self.response_frames[topic_id][key][0].signed_reply_payload.invoice.amount)
                for key in self.response_frames[topic_id].keys()]

    def pay_and_read_response(self, e, topic_id, replier_public_key):
        if not topic_id in self.response_frames:
            self.error(e, "topic has no responses")
            return
        if not replier_public_key in self.response_frames[topic_id]:
            self.error(e, "replier has not responsed for this topic")
            return
        self.info(e, "paying and reading")

        keys_for_payment_hashes: Dict[bytes, bytes] = dict()
        failed_payment_hashes: Set[bytes] = set()

        while True:
            network_fees = [sum(0 if inv.payment_hash in keys_for_payment_hashes else inv.amount
                                for inv in response_frame.invoices)
                            for response_frame in self.response_frames[topic_id][replier_public_key]
                            if all(not inv.payment_hash in failed_payment_hashes for inv in response_frame.invoices)
                            ]

            if len(network_fees) == 0:
                self.error(
                    e, "all payment routes failed, giving up, sorry :(")
                return None

            min_network_fee_idx = argmin(network_fees)
            response_frame = self.response_frames[topic_id][replier_public_key][min_network_fee_idx]

            failure = False
            for invoice in response_frame.invoices:
                self.info(e, invoice)
                proof_of_payment = self.payment_channel.pay_invoice(
                    invoice)
                if proof_of_payment is None:  # unsuccessful payment
                    self.error(
                        e, "cant pay for the invoice or decrypt the message")
                    failed_payment_hashes.add(invoice.payment_hash)
                    failure = True
                    break
                keys_for_payment_hashes[invoice.payment_hash] = proof_of_payment.preimage

            if not failure:
                message = response_frame.signed_reply_payload.encrypted_reply_message
                for invoice in response_frame.invoices:
                    key = keys_for_payment_hashes[invoice.payment_hash]
                    message = crypto.symmetric_decrypt(
                        key, message)
                message = crypto.decrypt_object(message, self._private_key)

                self.info(e, message)
                self.info(e, response_frame.invoices)
                self.info(e, response_frame.signed_reply_payload.invoice)
                return message


    def on_message(self, e, m):
        if isinstance(m.data, AskForBroadcastFrame):
            self.on_ask_for_broadcast_frame(e, m, m.sender, m.data)
        elif isinstance(m.data, POWBroadcastConditionsFrame):
            self.on_pow_broadcast_conditions_frame(e, m, m.sender, m.data)
        elif isinstance(m.data, POWBroadcastFrame):
            self.on_pow_broadcast_frame(e, m, m.sender, m.data)
        elif isinstance(m.data, ResponseFrame):
            self.on_response_frame(e, m, m.sender, m.data)
        else:
            self.trace(e, "unknown request:", m)